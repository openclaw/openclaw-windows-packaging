using System.Diagnostics;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Exercises the real <c>openclaw-session-host.exe</c> against real request and
/// result files, rather than only its internals.
/// </summary>
/// <remarks>
/// The helper launches itself, so the test needs no external interpreter and
/// stays deterministic. The outer invocation proves a real shell-free start
/// with an explicit working directory; the inner one deliberately fails so the
/// two control results distinguish "the application exited 64" from "the
/// helper never started it".
/// </remarks>
public sealed class SessionHostExecutableTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    private static string HelperPath
    {
        get
        {
            string path = Path.Combine(
                AppContext.BaseDirectory,
                "openclaw-session-host.exe");
            Assert.True(
                File.Exists(path),
                $"The guest helper was not copied next to the tests: {path}");
            return path;
        }
    }

    /// <summary>
    /// A directory whose name contains a space and literal percent signs, so a
    /// path that was expanded or split would not resolve.
    /// </summary>
    private string CreateHostileDirectory()
    {
        string path = Path.Combine(_testDirectory, "work dir %USERPROFILE%");
        Directory.CreateDirectory(path);
        return path;
    }

    private string StateDirectory => Path.Combine(_testDirectory, "state-transfer");

    // Windows resolves the profile's local application data from the registry,
    // so the helper's state gate is redirected through its own override instead
    // of writing lock files into the developer's real profile.
    private void IsolateState(ProcessStartInfo startInfo) =>
        startInfo.Environment["OPENCLAW_SESSION_STATE_DIRECTORY"] = StateDirectory;

    private int RunHelper(
        string requestPath,
        string workingDirectory,
        string mode = "--request")
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = HelperPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        IsolateState(startInfo);
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(requestPath);

        using Process process = Process.Start(startInfo)!;
        process.StandardError.ReadToEnd();
        Assert.True(
            process.WaitForExit(TimeSpan.FromSeconds(30)),
            "The guest helper did not exit.");
        return process.ExitCode;
    }

    private static SessionLaunchResult ReadResult(string requestPath) =>
        SessionLaunchProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));

    [Fact]
    public async Task AttachedHelperInitializesUtf8BeforeStartingTheChild()
    {
        string requestPath = Path.Combine(_testDirectory, "utf8 request.json");
        File.WriteAllText(
            requestPath,
            SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "utf8",
                Executable = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell", "v1.0", "powershell.exe"),
                Arguments =
                [
                    "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::WriteLine([string][char]0x00e9 + [char]0x4e2d)"
                ],
                WorkingDirectory = _testDirectory
            }));
        string bootstrapPath = Path.Combine(_testDirectory, "private-console.ps1");
        File.WriteAllText(bootstrapPath, """
            param([string]$Helper, [string]$Request)
            $ErrorActionPreference = 'Stop'
            Add-Type @'
            using System.Runtime.InteropServices;
            public static class FixtureConsole {
                [DllImport("kernel32.dll")] public static extern uint GetConsoleCP();
                [DllImport("kernel32.dll")] public static extern bool FreeConsole();
                [DllImport("kernel32.dll")] public static extern bool AllocConsole();
                [DllImport("kernel32.dll")] public static extern bool SetConsoleCP(uint page);
                [DllImport("kernel32.dll")] public static extern bool SetConsoleOutputCP(uint page);
            }
            '@
            if ([FixtureConsole]::GetConsoleCP() -ne 0) {
                if (-not [FixtureConsole]::FreeConsole()) { throw 'Detaching the fixture console failed.' }
            }
            if (-not [FixtureConsole]::AllocConsole()) { throw 'Allocating a private fixture console failed.' }
            if (-not [FixtureConsole]::SetConsoleCP(437) -or
                -not [FixtureConsole]::SetConsoleOutputCP(437)) {
                throw 'Selecting the fixture code page failed.'
            }
            $start = [Diagnostics.ProcessStartInfo]::new($Helper)
            $start.UseShellExecute = $false
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.ArgumentList.Add('--request')
            $start.ArgumentList.Add($Request)
            $child = [Diagnostics.Process]::Start($start)
            try {
                $child.StandardOutput.BaseStream.CopyTo([Console]::OpenStandardOutput())
                $child.StandardError.BaseStream.CopyTo([Console]::OpenStandardError())
                $child.WaitForExit()
                $code = $child.ExitCode
            }
            finally { $child.Dispose() }
            exit $code
            """);
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-File", bootstrapPath, HelperPath, requestPath },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        }) ?? throw new InvalidOperationException("Starting the private-console fixture failed.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(true);

        string stdout = await output.ConfigureAwait(true);
        string stderr = await error.ConfigureAwait(true);
        Assert.True(process.ExitCode == 0, $"Private-console fixture failed ({process.ExitCode}): {stderr}");
        Assert.Empty(stderr);
        Assert.Equal("\u00e9\u4e2d", stdout.Trim());
        Assert.True(ReadResult(requestPath).Launched);
    }

    [Fact]
    public void ThePublishedHelperDispatchesConfigReadinessWithoutStartingAChild()
    {
        string requestPath = Path.Combine(_testDirectory, "readiness.json");
        File.WriteAllText(requestPath, "{ not a request");

        int exitCode = RunHelper(
            requestPath,
            AppContext.BaseDirectory,
            "--check-config");

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        string result = File.ReadAllText(
            SessionLaunchProtocol.ResultPathFor(requestPath));
        Assert.Contains("\"error\":", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHelperStartsARealProcessAndDistinguishesItsFailureFromTheChildExitCode()
    {
        string workspace = CreateHostileDirectory();

        // The inner request is deliberately unusable, so the inner helper
        // fails and exits with the helper failure code.
        string innerRequestPath = Path.Combine(workspace, "inner request.json");
        File.WriteAllText(innerRequestPath, "{ not a request");

        string outerRequestPath = Path.Combine(workspace, "outer request.json");
        File.WriteAllText(
            outerRequestPath,
            SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "outer",
                Executable = HelperPath,
                Arguments = ["--request", innerRequestPath],
                WorkingDirectory = workspace
            }));

        int exitCode = RunHelper(outerRequestPath, AppContext.BaseDirectory);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);

        // The launch went through the state gate, and only through the
        // fixture's redirected directory.
        Assert.True(File.Exists(Path.Combine(StateDirectory, "access.lock")));

        SessionLaunchResult outer = ReadResult(outerRequestPath);
        Assert.Equal("outer", outer.RequestId);
        Assert.True(outer.Launched);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, outer.ExitCode);

        SessionLaunchResult inner = ReadResult(innerRequestPath);
        Assert.False(inner.Launched);
        Assert.Null(inner.ExitCode);
        Assert.NotNull(inner.Error);
    }

    [Fact]
    public void AMissingWorkingDirectoryFailsInsteadOfRunningSomewhereElse()
    {
        // Execution does not inherit the caller's directory, so silently
        // continuing would run the command in the system directory.
        string requestPath = Path.Combine(_testDirectory, "request.json");
        File.WriteAllText(
            requestPath,
            SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "missing-cwd",
                Executable = HelperPath,
                Arguments = [],
                WorkingDirectory = Path.Combine(_testDirectory, "absent")
            }));

        int exitCode = RunHelper(requestPath, AppContext.BaseDirectory);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);

        SessionLaunchResult result = ReadResult(requestPath);
        Assert.False(result.Launched);
        Assert.Contains("working directory", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRequestFileIsRecordedInTheControlResult()
    {
        string requestPath = Path.Combine(_testDirectory, "absent.json");

        int exitCode = RunHelper(requestPath, AppContext.BaseDirectory);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
        Assert.True(File.Exists(SessionLaunchProtocol.ResultPathFor(requestPath)));

        SessionLaunchResult result = ReadResult(requestPath);
        Assert.False(result.Launched);
    }

    [Fact]
    public async Task DetachedLaunchClosesHelperOutputWhileSupervisorAndChildLive()
    {
        string workspace = CreateHostileDirectory();
        string requestPath = Path.Combine(workspace, "detached request.json");
        string logPath = Path.Combine(workspace, "gateway.log");
        string statusPath = Path.Combine(workspace, "gateway.status.json");
        string powershell = Path.Combine(
            Environment.SystemDirectory,
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        File.WriteAllText(
            requestPath,
            SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
            {
                RequestId = "detached",
                Mode = SessionLaunchMode.Detached,
                Executable = powershell,
                Arguments =
                [
                    "-NoLogo",
                    "-NoProfile",
                    "-Command",
                    "Start-Sleep -Seconds 30"
                ],
                WorkingDirectory = workspace,
                LogPath = logPath,
                StatusPath = statusPath
            }));

        ProcessStartInfo startInfo = new()
        {
            FileName = HelperPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        IsolateState(startInfo);
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);

        using Process helper = Process.Start(startInfo)!;
        Task<string> standardOutput = helper.StandardOutput.ReadToEndAsync();
        Task<string> standardError = helper.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Process? supervisor = null;
        try
        {
            await helper.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, helper.ExitCode);
            Assert.Empty(await standardOutput.WaitAsync(timeout.Token));
            Assert.Empty(await standardError.WaitAsync(timeout.Token));

            SessionLaunchResult result = ReadResult(requestPath);
            Assert.NotNull(result.ProcessId);
            supervisor = Process.GetProcessById(result.ProcessId.Value);
            Assert.False(supervisor.HasExited);
        }
        finally
        {
            if (supervisor is null &&
                File.Exists(SessionLaunchProtocol.ResultPathFor(requestPath)))
            {
                SessionLaunchResult result = ReadResult(requestPath);
                if (result.ProcessId is int processId)
                {
                    try
                    {
                        supervisor = Process.GetProcessById(processId);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            if (supervisor is not null && !supervisor.HasExited)
            {
                supervisor.Kill(entireProcessTree: true);
                await supervisor.WaitForExitAsync();
            }

            supervisor?.Dispose();
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
