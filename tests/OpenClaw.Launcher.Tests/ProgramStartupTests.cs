using System.Text;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests;

// Startup is the part of the host that owns diagnostics, argument routing, the
// operational error boundary, and disposal. These scenarios drive the real
// Program.RunAsync with fixture-owned storage, so a regression there is caught
// without a test ever writing to the developer's profile.
public sealed class ProgramStartupTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void ProductionStartupProvidesTheLifecycleFactoryForFallbackValidation()
    {
        Assert.NotNull(HostStartup.CreateProduction().InstallationLifecycle);
    }

    [Fact]
    public void ProductionStartupIdentifiesItsProcessConsoleWriters()
    {
        Assert.True(HostStartup.CreateProduction().UsesProcessConsoleWriters);
    }

    [Fact]
    public async Task StartupWritesRecordsToTheSuppliedDiagnosticLog()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");

        int exitCode = await Program.RunAsync(
            ["--version"],
            CreateStartup(logPath, output, error));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(logPath));
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Host started", log, StringComparison.Ordinal);
        Assert.Contains("Host exiting", log, StringComparison.Ordinal);
    }

    // The library's default exception handler is disabled so this boundary,
    // not System.CommandLine, reports operational failures.
    [Fact]
    public async Task OperationalFailureRecommendsCollectingLogsAndExitsNonZero()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");

        int exitCode = await Program.RunAsync(
            ["setup"],
            CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Contains("clawctl collect-logs", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/openclaw/openclaw-windows-packaging/issues",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("package is ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "Unhandled failure",
            await File.ReadAllTextAsync(logPath),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationalFailureStillExitsAndDisposesDiagnosticsWhenRenderingFails(
        bool disposed)
    {
        using var output = new StringWriter();
        TextWriter error = disposed
            ? new UnavailableTextWriter(new ObjectDisposedException("stderr"))
            : new UnavailableTextWriter(new IOException("stderr is unavailable"));
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        HostDiagnosticLog diagnostics = HostDiagnosticLog.Create(logPath);
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => diagnostics,
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(["setup"], startup);

        Assert.Equal(1, exitCode);
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Unhandled failure", log, StringComparison.Ordinal);

        // The failed write is logged with its message, not only its type, so
        // a lost error report is still diagnosable from the bundle.
        Assert.Contains(
            disposed
                ? "Failure output to standard error failed: ObjectDisposedException: "
                : "Failure output to standard error failed: IOException: stderr is unavailable",
            log,
            StringComparison.Ordinal);
        if (disposed)
        {
            Assert.Contains("'stderr'", log, StringComparison.Ordinal);
        }

        Assert.Throws<ObjectDisposedException>(() => diagnostics.Write("after return"));
    }

    [Fact]
    public async Task JsonFailureStillExitsWhenStandardOutputFails()
    {
        using var error = new StringWriter();
        var output = new UnavailableTextWriter(new IOException("stdout is unavailable"));
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        HostDiagnosticLog diagnostics = HostDiagnosticLog.Create(logPath);
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => diagnostics,
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(["setup", "--json"], startup);

        Assert.Equal(1, exitCode);
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Unhandled failure", log, StringComparison.Ordinal);
        Assert.Contains(
            "Failure output to standard output failed: IOException: stdout is unavailable",
            log,
            StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => diagnostics.Write("after return"));
    }

    [Theory]
    [InlineData("status --json", "status")]
    [InlineData("--json status", "status")]
    [InlineData("gateway-service start --json", "gateway-service start")]
    [InlineData("gateway-service restart --json", "gateway-service restart")]
    public async Task JsonOperationalFailurePreservesTheSelectedCommand(
        string commandLine,
        string expectedCommand)
    {
        string[] args = commandLine.Split(' ');
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", $"{Guid.NewGuid():N}.log");

        int exitCode = await Program.RunAsync(args, CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedCommand, document.RootElement.GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("--json=true", true)]
    [InlineData("--json:true", true)]
    [InlineData("--json false", false)]
    public async Task OperationalFailureHonorsParsedJsonBoolean(
        string option,
        bool expectedJson)
    {
        string[] args = option.Contains(' ', StringComparison.Ordinal)
            ? ["status", .. option.Split(' ')]
            : ["status", option];
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", $"{Guid.NewGuid():N}.log");

        int exitCode = await Program.RunAsync(args, CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Equal(expectedJson, !string.IsNullOrWhiteSpace(output.ToString()));
        Assert.Equal(!expectedJson, !string.IsNullOrWhiteSpace(error.ToString()));
        if (expectedJson)
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        }
    }

    [Theory]
    [InlineData("--json=true", true)]
    [InlineData("--json:true", true)]
    [InlineData("--json false", false)]
    public async Task PackageDiscoveryFailureHonorsJsonBoolean(
        string option,
        bool expectedJson)
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string architecture = System.Runtime.InteropServices.RuntimeInformation
            .ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            var value => value.ToString()
        };
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, $"node-v24.0.0-win-{architecture}.zip"),
            string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, $"node-v24.1.0-win-{architecture}.zip"),
            string.Empty);
        string[] args = option.Contains(' ', StringComparison.Ordinal)
            ? ["status", .. option.Split(' ')]
            : ["status", option];
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", $"{Guid.NewGuid():N}.log");

        int exitCode = await Program.RunAsync(args, CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Equal(expectedJson, !string.IsNullOrWhiteSpace(output.ToString()));
        Assert.Equal(!expectedJson, !string.IsNullOrWhiteSpace(error.ToString()));
        if (expectedJson)
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        }
    }

    // Losing the log must not take the command down with it: the host warns
    // once and keeps running.
    [Fact]
    public async Task UnavailableDiagnosticsWarnsOnceAndStillRunsTheCommand()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => throw new IOException("The log is unavailable."),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(["--version"], startup);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "Unable to create diagnostics",
            error.ToString(),
            StringComparison.Ordinal);

        // The point of the test is that the command still produced its result
        // after diagnostics failed, not what the version happens to be.
        Assert.Contains(
            ClawCtlBuildMetadata.PackageVersion,
            output.ToString(),
            StringComparison.Ordinal);
    }

    // Startup routes the agent alias without letting the clawctl parser see
    // the arguments. On a machine that cannot host a session it fails loudly,
    // and the failure is the host's, not the parser's.
    [Fact]
    public async Task AgentStartupFailsLoudlyWithoutParsingItsArguments()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        string[] arguments = ["--help", "--", "@file.rsp", "[suggest:1]", "", "a b"];
        using var output = new StringWriter();
        using var error = new StringWriter();

        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Agent,
            CreateDiagnostics = () => HostDiagnosticLog.Create(logPath),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(arguments, startup);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains(logPath, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "newer version of Windows",
            error.ToString(),
            StringComparison.Ordinal);
    }

    private HostStartup CreateStartup(
        string logPath,
        TextWriter output,
        TextWriter error) => new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => HostDiagnosticLog.Create(logPath),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

    // A field report arrived as "Unhandled failure: MxcException" and nothing
    // else: setup had provisioned the session and MXC refused to start it, but
    // the bundle named neither the backend's error nor the build it ran on.
    [Fact]
    public async Task SetupFailureLogsTheBackendErrorThrowSiteAndEnvironment()
    {
        string baseDirectory = Path.Combine(_testDirectory, "package");
        Directory.CreateDirectory(Path.Combine(baseDirectory, "app"));
        await File.WriteAllTextAsync(
            Path.Combine(baseDirectory, "app", "openclaw.mjs"),
            "fixture");
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        await File.WriteAllTextAsync(helperPath, "fixture");
        string workspace = Path.Combine(_testDirectory, "workspace");
        var executor = new LifecyclePhaseExecutor(
            provision:
                "{\"result\":{\"sandboxId\":\"iso:fixture\",\"metadata\":{\"agentUserName\":\"agent_1\"," +
                "\"agentUserSid\":\"S-1-5-21-0-0-0-1001\",\"ephemeralWorkspacePath\":" +
                System.Text.Json.JsonSerializer.Serialize(workspace) +
                "}}}",
            start:
                """
                {"error":{"code":"backend_error","message":"The session could not be started.",
                "operation":"IsoSessionOps.StartSessionAsync","nativeCode":"0x80040233",
                "remediation":"Start it from an interactive session."}}
                """);
        SessionRuntime runtime = SessionRuntime.Create(
            HostPaths.ForRoot(Path.Combine(_testDirectory, "state"), "OpenClaw.Gateway_startup"),
            () => throw new InvalidOperationException("The test supplies its backend."),
            baseDirectory,
            _ => { },
            new MxcCliSessionClient(
                new MxcRuntimeLocation(
                    Path.Combine(baseDirectory, "mxc"),
                    Path.Combine(baseDirectory, "mxc", "wxc-exec.exe"),
                    Path.Combine(baseDirectory, "mxc", "plm.exe"),
                    null),
                executor));
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => HostDiagnosticLog.Create(logPath),
            BaseDirectory = baseDirectory,
            Output = output,
            Error = error,
            InstallationLifecycle = new SupportedHostLifecycle(runtime),
            ReadEnvironment = _ => new HostEnvironment(
                "10.0.26340.9212 (X64 OS, X64 process)",
                "OpenClaw.Gateway_2026.9.4.1003_x64__fixture",
                "@microsoft/mxc-sdk 0.8.0 x64, wire 0.6.0-alpha",
                "24.20.0",
                ".NET fixture")
        };

        int exitCode = await Program.RunAsync(["setup"], startup);

        Assert.Equal(1, exitCode);
        Assert.Equal(["provision", "start"], executor.Phases);
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains(
            "Environment: Windows 10.0.26340.9212 (X64 OS, X64 process); " +
            "package OpenClaw.Gateway_2026.9.4.1003_x64__fixture, build " +
            $"{ClawCtlBuildMetadata.PackageVersion} (commit {ClawCtlBuildMetadata.PackageCommit}); " +
            $"OpenClaw payload {ClawCtlBuildMetadata.PayloadVersion} " +
            $"(commit {ClawCtlBuildMetadata.PayloadCommit}); " +
            "MXC @microsoft/mxc-sdk 0.8.0 x64, wire 0.6.0-alpha; Node.js 24.20.0; .NET fixture",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unhandled failure: MxcException: The session could not be started. " +
            "Start it from an interactive session. (native code 0x80040233) " +
            "[code BackendError; backend code backend_error; " +
            "operation IsoSessionOps.StartSessionAsync]",
            log,
            StringComparison.Ordinal);
        Assert.Matches(@"\r?\n   at ", log);
        Assert.Contains(
            "The session could not be started.",
            error.ToString(),
            StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class UnavailableTextWriter(Exception exception) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw exception;
    }

    /// <summary>
    /// Answers each MXC lifecycle phase with a fixed envelope, standing in for
    /// the executor process so the real client and wire parsing run.
    /// </summary>
    private sealed class LifecyclePhaseExecutor(string provision, string start)
        : IMxcExecutorInvoker
    {
        public List<string> Phases { get; } = [];

        public Task<MxcExecutorOutcome> InvokeAsync(
            MxcExecutorInvocation invocation,
            CancellationToken cancellationToken)
        {
            string phase = MxcWireProtocol.DecodeConfig(invocation.Arguments[1]).Phase!;
            Phases.Add(phase);
            return Task.FromResult(new MxcExecutorOutcome(
                phase == MxcWireProtocol.StartPhase ? 1 : 0,
                phase switch
                {
                    MxcWireProtocol.ProvisionPhase => provision,
                    MxcWireProtocol.StartPhase => start,
                    _ => throw new InvalidOperationException($"Unexpected MXC phase '{phase}'.")
                },
                string.Empty));
        }
    }

    /// <summary>
    /// Reports the host as supported and supplies the runtime under test.
    /// Setup must fail before anything else here is reached.
    /// </summary>
    private sealed class SupportedHostLifecycle(SessionRuntime runtime)
        : IInstallationLifecycle
    {
        public SessionRuntime CreateRuntime(Action<string> log) => runtime;

        public Task EnsureSessionSupportedAsync(
            Action<string> log,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public PackageRuntimeMetadata ValidatePackageRuntime(
            HostOptions options,
            SessionRuntime sessionRuntime) =>
            throw new NotSupportedException();

        public ISessionLockHandle AcquireLifecycleLock(
            SessionRuntime sessionRuntime) =>
            throw new NotSupportedException();

        public Task<TeardownResult> TeardownAsync(
            HostOptions options,
            SessionRuntime sessionRuntime,
            Action<string> log,
            bool lockAlreadyHeld,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IInstallationStateCleaner CreateStateCleaner(
            SessionRuntime sessionRuntime) =>
            throw new NotSupportedException();

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
