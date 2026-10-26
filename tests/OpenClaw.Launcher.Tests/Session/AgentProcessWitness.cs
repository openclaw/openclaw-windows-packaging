using System.Text.Json;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

// Explicit disposable-session proof only: the observer runs as the agent, so
// pinning process handles requires no host-observer process DACL grants.
internal sealed class AgentProcessWitness(
    Task<MxcExecutionResult> execution, string directory, string agentSid,
    Action<string> report, CancellationToken deadline) : IAsyncDisposable
{
    private static readonly string[] Groups = ["first", "sibling"];

    public static async Task<AgentProcessWitness> StartAsync(
        IMxcSessionClient client, SessionRecord record, string helper,
        string first, string sibling, Action<string> report, CancellationToken deadline)
    {
        string directory = Path.Combine(record.WorkspacePath!, "witness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "witness.ps1");
        string request = Path.Combine(directory, "witness.json");
        File.WriteAllText(script, Script);
        File.WriteAllText(request, SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
        {
            RequestId = "agent-witness-" + Guid.NewGuid().ToString("N"),
            Executable = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script,
                "-Root", directory, "-First", first, "-Sibling", sibling],
            WorkingDirectory = directory,
            Environment = CancellationPrototypeProbe.FixtureEnvironment(directory, grantHostObservation: false)
        }));
        using var ready = new CancellationPrototypeProbe.FileArrival(directory, "before.ready");
        Task<MxcExecutionResult> execution = client.ExecuteAsync(record.ToSandboxIdOrThrow(),
            new MxcExecutionRequest(SessionExecutor.BuildGuestCommandLine(helper, request)), CancellationToken.None);
        var witness = new AgentProcessWitness(execution, directory, record.AgentUserSid!, report, deadline);
        try
        {
            await witness.WaitAsync(ready).ConfigureAwait(false);
            witness.AssertSnapshot("before", firstExited: false, siblingExited: false);
            return witness;
        }
        catch
        {
            await witness.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task CheckFirstAsync() => CheckAsync("first", firstExited: true, siblingExited: false);
    public Task CheckSiblingAsync() => CheckAsync("sibling", firstExited: true, siblingExited: true);

    private async Task CheckAsync(string phase, bool firstExited, bool siblingExited)
    {
        using var ready = new CancellationPrototypeProbe.FileArrival(directory, phase + ".ready");
        File.WriteAllText(Path.Combine(directory, phase + ".check"), "");
        await WaitAsync(ready).ConfigureAwait(false);
        AssertSnapshot(phase, firstExited, siblingExited);
    }

    private async Task WaitAsync(CancellationPrototypeProbe.FileArrival ready)
    {
        Task arrival = ready.WaitAsync(deadline);
        if (await Task.WhenAny(arrival, execution).ConfigureAwait(false) == execution && !arrival.IsCompleted)
        {
            if (ready.IsPresent)
            {
                return;
            }
            MxcExecutionResult result = await execution.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Agent witness exited before its observation (exit {result.ExitCode}): {result.StandardError}");
        }
        await arrival.ConfigureAwait(false);
    }

    private void AssertSnapshot(string phase, bool firstExited, bool siblingExited)
    {
        string text = File.ReadAllText(Path.Combine(directory, phase + ".json"));
        report($"NATURAL_RIGHTS phase={phase} {text}");
        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        Assert.Equal(agentSid, root.GetProperty("agentSid").GetString());
        JsonElement rows = root.GetProperty("rows");
        Assert.Equal(10, rows.GetArrayLength());
        Assert.Equal(10, rows.EnumerateArray().Select(row => row.GetProperty("pid").GetInt32()).Distinct().Count());
        string[] expectedRoles = ["child", "coordinator", "descendant", "helper", "parent"];
        foreach (string group in Groups)
        {
            string[] roles = [.. rows.EnumerateArray()
                .Where(row => row.GetProperty("group").GetString() == group)
                .Select(row => row.GetProperty("role").GetString()
                    ?? throw new InvalidOperationException("The agent witness omitted a process role."))
                .Order(StringComparer.Ordinal)];
            Assert.Equal(expectedRoles, roles);
        }
        foreach (JsonElement row in rows.EnumerateArray())
        {
            string? group = row.GetProperty("group").GetString();
            Assert.Contains(group, Groups);
            bool coordinator = row.GetProperty("role").GetString() == "coordinator";
            Assert.Equal(!coordinator, row.GetProperty("memberAtPin").GetBoolean());
            Assert.Equal(0, row.GetProperty("error").GetInt32());
            bool exited = group == "first" ? firstExited : siblingExited;
            Assert.Equal(exited ? 0u : 258u, row.GetProperty("status").GetUInt32());
        }
    }

    public async ValueTask DisposeAsync()
    {
        File.WriteAllText(Path.Combine(directory, "first.check"), "");
        File.WriteAllText(Path.Combine(directory, "sibling.check"), "");
        MxcExecutionResult result = await execution.WaitAsync(deadline).ConfigureAwait(false);
        Assert.True(result.ExitCode == 0,
            $"Agent witness failed (exit {result.ExitCode}): {result.StandardError}");
    }

    private const string Script = """
        param([string]$Root, [string]$First, [string]$Sibling)
        $ErrorActionPreference = 'Stop'
        Add-Type @'
        using System;
        using System.Runtime.InteropServices;
        using Microsoft.Win32.SafeHandles;
        public static class AgentWitnessNative {
            [DllImport("kernel32.dll", SetLastError=true)]
            public static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
            [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
            public static extern SafeFileHandle OpenJobObjectW(uint access, bool inherit, string name);
            [DllImport("kernel32.dll", SetLastError=true)]
            public static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job, out bool member);
            [DllImport("kernel32.dll", SetLastError=true)]
            public static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        }
        '@
        function Wait-File([string]$Name) {
            $watch = [IO.FileSystemWatcher]::new($Root, $Name)
            $signal = [guid]::NewGuid().ToString('N')
            $null = Register-ObjectEvent -InputObject $watch -EventName Created -SourceIdentifier $signal
            $watch.EnableRaisingEvents = $true
            try {
                if (-not [IO.File]::Exists((Join-Path $Root $Name))) {
                    if ($null -eq (Wait-Event -SourceIdentifier $signal -Timeout 60)) {
                        throw "Agent witness signal timed out: $Name"
                    }
                }
            } finally {
                Unregister-Event -SourceIdentifier $signal
                Remove-Event -SourceIdentifier $signal -ErrorAction SilentlyContinue
                $watch.Dispose()
            }
        }
        $pinned = [Collections.Generic.List[object]]::new()
        function Publish([string]$Phase) {
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            try { $sid = $identity.User.Value } finally { $identity.Dispose() }
            $rows = foreach ($process in $pinned) {
                $status = [AgentWitnessNative]::WaitForSingleObject($process.Handle, 0)
                $nativeError = if ($status -eq [uint32]::MaxValue) {
                    [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                } else { 0 }
                @{
                    group = $process.Group; role = $process.Role; pid = $process.ProcessId
                    memberAtPin = $process.Member; status = $status; error = $nativeError
                }
            }
            $text = @{ agentSid = $sid; rows = @($rows) } | ConvertTo-Json -Depth 5 -Compress
            [IO.File]::WriteAllText((Join-Path $Root "$Phase.json"), $text)
            [IO.File]::WriteAllText((Join-Path $Root "$Phase.ready"), '')
        }
        try {
            foreach ($group in @(@{Name='first';Root=$First}, @{Name='sibling';Root=$Sibling})) {
                $mode = [IO.File]::ReadAllText((Join-Path $group.Root 'invocation-mode.txt')).Trim()
                if ($mode -notmatch '^--request-in-job=([0-9a-fA-F]{32})$') {
                    throw 'Agent witness received an invalid invocation mode.'
                }
                $job = [AgentWitnessNative]::OpenJobObjectW(4, $false, ('Global\OpenClaw.Invocation.' + $Matches[1]))
                try {
                    if ($job.IsInvalid) {
                        throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                    }
                    foreach ($role in @('helper','parent','child','descendant','coordinator')) {
                        $file = if ($role -eq 'coordinator') { 'coordinator-process.txt' } else { "$role.pid" }
                        $processId = [int][IO.File]::ReadAllText((Join-Path $group.Root $file))
                        $handle = [AgentWitnessNative]::OpenProcess(0x00101000, $false, $processId)
                        if ($handle.IsInvalid) {
                            $nativeError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
                            $handle.Dispose()
                            throw [ComponentModel.Win32Exception]::new($nativeError)
                        }
                        $record = [pscustomobject]@{
                            Group=$group.Name;Role=$role;ProcessId=$processId;Handle=$handle;Member=$false
                        }
                        $pinned.Add($record)
                        $member = $false
                        if (-not [AgentWitnessNative]::IsProcessInJob($handle, $job, [ref]$member)) {
                            throw [ComponentModel.Win32Exception]::new([Runtime.InteropServices.Marshal]::GetLastWin32Error())
                        }
                        $record.Member = $member
                    }
                } finally { $job.Dispose() }
            }
            Publish 'before'
            Wait-File 'first.check'
            Publish 'first'
            Wait-File 'sibling.check'
            Publish 'sibling'
        } finally {
            foreach ($process in $pinned) { $process.Handle.Dispose() }
        }
        """;
}
