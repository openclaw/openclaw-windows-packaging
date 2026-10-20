using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class InvocationConsoleCancellationTests
{
    [Fact]
    public async Task ConsoleCtrlCLeavesTheCoordinatorAliveToConfirmDrain()
    {
        string root = TestDirectory.Create();
        string eventPrefix = @"Local\OpenClaw.ConsoleTest." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".release");
        using var observed = new EventWaitHandle(false, EventResetMode.ManualReset, eventPrefix + ".observed");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using WindowsInvocationJob job = WindowsInvocationJob.Create(identity.User!.Value);
        string shell = AgentShellResolver.Resolve(File.Exists).ExecutablePath;
        string helper = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe");
        string workload = Path.Combine(root, "control.ps1");
        string bootstrap = Path.Combine(root, "console.ps1");
        string request = Path.Combine(root, "request.json");
        File.WriteAllText(workload, WorkloadScript);
        File.WriteAllText(bootstrap, BootstrapScript);
        File.WriteAllText(request, SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
        {
            RequestId = "console-cancellation",
            Executable = shell,
            Arguments = ["-NoLogo", "-NoProfile", "-File", workload, "-Events", eventPrefix],
            WorkingDirectory = root,
            Mode = SessionLaunchMode.Attached
        }));
        ProcessStartInfo start = new(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        foreach (string argument in new[]
        {
            "-NoLogo", "-NoProfile", "-File", bootstrap,
            "-Helper", helper, "-Mode", job.Mode, "-Request", request
        })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["TEMP"] = root;
        start.Environment["TMP"] = root;
        start.Environment["PSModuleAnalysisCachePath"] = Path.Combine(root, "module-cache");
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["POWERSHELL_UPDATECHECK"] = "Off";
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var stopped = new EventWaitHandle(false, EventResetMode.ManualReset)
        {
            SafeWaitHandle = new SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false)
        };
        job.MarkDispatched();
        Task exit = ObserveExitAsync();
        try
        {
            int state = WaitHandle.WaitAny([ready, stopped], TimeSpan.FromSeconds(30));
            Assert.True(state == 0, state == 1
                ? $"The private-console fixture exited: {await stderr.ConfigureAwait(true)}"
                : "The private-console workload never became ready.");
            release.Set();
            Assert.True(observed.WaitOne(TimeSpan.FromSeconds(30)), "The real console control did not reach the workload.");
            job.Stop();
            await exit.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            _ = await stdout.ConfigureAwait(true);
            _ = await stderr.ConfigureAwait(true);
        }
        finally
        {
            release.Set();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            try
            {
                await exit.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            }
            catch (InvalidOperationException) when (exit.IsFaulted)
            {
                // The assertion/Stop failure above owns the unconfirmed-drain diagnostic.
            }
            Directory.Delete(root, recursive: true);
        }

        async Task ObserveExitAsync()
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            job.ForegroundExited();
        }
    }

    private const string BootstrapScript = """
        param([string]$Helper, [string]$Mode, [string]$Request)
        $ErrorActionPreference = 'Stop'
        Add-Type @'
        using System;
        using System.Runtime.InteropServices;
        public static class PrivateConsole {
            [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
            [DllImport("kernel32.dll")] static extern uint GetConsoleCP();
            [DllImport("kernel32.dll")] static extern bool FreeConsole();
            [DllImport("kernel32.dll")] static extern bool AllocConsole();
            [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
            public static void Initialize() {
                if (GetConsoleCP() != 0 && !FreeConsole()) throw new InvalidOperationException("Private fixture detachment failed.");
                if (!AllocConsole()) throw new InvalidOperationException("Private console allocation failed.");
                ShowWindow(GetConsoleWindow(), 0);
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs args) { args.Cancel = true; };
            }
        }
        '@
        [PrivateConsole]::Initialize()
        $start = [Diagnostics.ProcessStartInfo]::new($Helper)
        $start.UseShellExecute = $false
        $start.Arguments = '"' + $Mode + '" "' + $Request + '"'
        $process = [Diagnostics.Process]::Start($start)
        try {
            $process.WaitForExit()
            exit $process.ExitCode
        }
        finally { $process.Dispose() }
        """;

    private const string WorkloadScript = """
        param([string]$Events)
        $ErrorActionPreference = 'Stop'
        Add-Type @'
        using System;
        using System.ComponentModel;
        using System.Runtime.InteropServices;
        using System.Threading;
        public static class RealConsoleControl {
            [DllImport("kernel32.dll", SetLastError=true)]
            static extern bool GenerateConsoleCtrlEvent(uint control, uint group);
            public static void Run(string events) {
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs args) {
                    using (var observed = EventWaitHandle.OpenExisting(events + ".observed")) observed.Set();
                    args.Cancel = true;
                    Environment.Exit(130);
                };
                using (var ready = EventWaitHandle.OpenExisting(events + ".ready")) ready.Set();
                using (var release = EventWaitHandle.OpenExisting(events + ".release")) release.WaitOne();
                if (!GenerateConsoleCtrlEvent(0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
                using (var exit = new ManualResetEvent(false)) exit.WaitOne();
            }
        }
        '@
        [RealConsoleControl]::Run($Events)
        """;
}
