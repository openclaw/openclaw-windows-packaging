using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;
using GuestProgram = OpenClaw.SessionHost.Program;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Drives a package update through the real host commands and the real guest
/// helper, faking only the isolated-session backend and the process start.
/// </summary>
/// <remarks>
/// Package A mirrors fs-safe 0.25: only its architecture package carries a
/// native. Package B mirrors 0.26.5: the JavaScript package gains a helper
/// executable and worker, so it is staged too, and the native changes. Both
/// ship the same Node.js archive, so the recorded runtime cannot reveal the
/// update.
/// </remarks>
public sealed partial class NativeStageUpgradeScenarioTests : IDisposable
{
    private const string NativePackage = "node_modules/@openclaw/fs-safe-win32-x64-msvc";
    private const string JavaScriptPackage = "node_modules/@openclaw/fs-safe";

    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend;
    private readonly RecordingLauncher _launcher = new();
    private readonly SessionRuntime _runtime;
    private readonly string _agentLocal;

    public NativeStageUpgradeScenarioTests()
    {
        _agentLocal = Path.Combine(_root, "agent-local");
        string baseDirectory = Path.Combine(_root, "base");
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "fixture helper");
        string workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(workspace);
        _backend = new FakeMxcSessionClient
        {
            Metadata = new MxcProvisionMetadata("agent_1", "S-1-5-21-0-0-0-1001", workspace)
        };
        _backend.ExecuteBehavior = ExecuteInGuest;
        _backend.AttachedBehavior = _ =>
            Task.FromResult(RunGuestLaunch(_backend.AttachedCommandLines[^1]));
        _runtime = SessionRuntime.Create(
            HostPaths.ForRoot(Path.Combine(_root, "state"), "OpenClaw.Gateway_test"),
            () => throw new InvalidOperationException("The test backend must be supplied."),
            baseDirectory,
            _ => { },
            _backend,
            isInteractiveLogon: () => true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnUpdatedPackageIsRefusedUntilSetupRestagesItInTheSameSession()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        SetupRecord setupA = ReadSetup();
        Assert.Equal(0, await RunAsync(a, "pwsh", "--command", "exit"));
        Assert.Equal(setupA.AgentNativeRoot, Assert.Single(_launcher.Runs).NativeStage!.RootPath);

        HostOptions b = CreatePackage("pkg-b", PackageB());
        string setupBefore = File.ReadAllText(_runtime.Paths.SetupStatePath);
        string sessionBefore = File.ReadAllText(_runtime.Paths.SessionStatePath);

        NativeStageStaleException shell = await Assert.ThrowsAsync<NativeStageStaleException>(
            () => RunAsync(b, "pwsh", "--command", "exit"));
        NativeStageStaleException gateway = await Assert.ThrowsAsync<NativeStageStaleException>(
            () => RunAsync(b, "gateway-service", "start"));

        foreach (NativeStageStaleException refusal in new[] { shell, gateway })
        {
            Assert.Contains("stale-source", refusal.Message, StringComparison.Ordinal);
            Assert.Contains("Run `clawctl setup`", refusal.Message, StringComparison.Ordinal);
        }

        // Nothing started, and neither the setup, the session, nor a retained
        // gateway launch attempt records the refused launches.
        Assert.Single(_launcher.Runs);
        Assert.Empty(_launcher.Starts);
        Assert.Equal(GatewayStateFault.Missing, _runtime.GatewayState.Read().Fault);
        Assert.Equal(setupBefore, File.ReadAllText(_runtime.Paths.SetupStatePath));
        Assert.Equal(sessionBefore, File.ReadAllText(_runtime.Paths.SessionStatePath));

        Assert.Equal(0, await RunAsync(b, "setup"));
        SetupRecord setupB = ReadSetup();
        Assert.Equal(1, _backend.Calls.Count(call => call == "provision"));
        Assert.Equal(setupA.SandboxId, setupB.SandboxId);
        Assert.Equal(setupA.AgentNodeArchive, setupB.AgentNodeArchive);
        Assert.NotEqual(setupA.AgentNativeRoot, setupB.AgentNativeRoot);
        Assert.Equal(
            "helper b",
            File.ReadAllText(Path.Combine(
                setupB.AgentNativeRoot!,
                @"node_modules\@openclaw\fs-safe\dist\windows-security-helper.exe")));

        Assert.Equal(0, await RunAsync(b, "pwsh", "--command", "exit"));
        Assert.Equal(0, await RunAsync(b, "gateway-service", "start"));
        Assert.Equal(setupB.AgentNativeRoot, _launcher.Runs[^1].NativeStage!.RootPath);
        Assert.Equal(setupB.AgentNativeRoot, Assert.Single(_launcher.Starts).NativeStage!.RootPath);
    }

    /// <summary>
    /// Companion reads <c>error.type</c>; a refusal must stay distinguishable
    /// from an arbitrary failure when it reaches the process boundary.
    /// </summary>
    [Fact]
    public async Task AJsonGatewayStartReportsTheTypedRefusal()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        HostOptions b = CreatePackage("pkg-b", PackageB());
        using var output = new StringWriter();

        int exitCode = await OpenClaw.Launcher.Program.RunAsync(
            ["gateway-service", "start", "--json"],
            new HostStartup
            {
                Entrypoint = HostEntrypoint.Control,
                CreateDiagnostics = () => HostDiagnosticLog.Create(
                    Path.Combine(_root, "logs", "openclaw.log")),
                BaseDirectory = Path.GetDirectoryName(b.PackagedApplicationDirectory)!,
                Output = output,
                Error = TextWriter.Null,
                InstallationLifecycle = new ScenarioLifecycle(_runtime)
            });

        Assert.Equal(1, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement error = document.RootElement.GetProperty("error");
        Assert.Equal("native_stage_stale", error.GetProperty("type").GetString());
        Assert.Contains("clawctl setup", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(_launcher.Starts);
    }

    [Fact]
    public async Task CompanionPrepareReportsTheTypedRefusalWithoutTouchingConfiguration()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        HostOptions b = CreatePackage("pkg-b", PackageB());
        using var output = new StringWriter();

        int exitCode = await OpenClaw.Launcher.Program.RunControlAsync(
            b,
            ["companion", "prepare", "--port", "19001", "--json"],
            _ => { },
            output,
            TextWriter.Null,
            new ScenarioLifecycle(_runtime),
            readEnvironmentVariable: _ => null,
            controlOutputOptions: new ClawCtlOutputOptions { Json = true });

        Assert.Equal(1, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("native_stage_stale", document.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.False(Directory.Exists(Path.Combine(_root, "agent-profile", ".openclaw")));
    }

    /// <summary>
    /// Backup runs packaged Node.js, so its refusal must reach JSON callers
    /// with the same type as every other launch.
    /// </summary>
    [Fact]
    public async Task AJsonBackupReportsTheTypedRefusal()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        HostOptions b = CreatePackage("pkg-b", PackageB());
        using var output = new StringWriter();

        int exitCode = await OpenClaw.Launcher.Program.RunControlAsync(
            b,
            ["backup", "--json"],
            _ => { },
            output,
            TextWriter.Null,
            new ScenarioLifecycle(_runtime),
            readEnvironmentVariable: _ => null,
            controlOutputOptions: new ClawCtlOutputOptions { Json = true },
            archiveDirectory: Path.Combine(_root, "archives"));

        Assert.Equal(1, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement error = document.RootElement.GetProperty("error");
        Assert.Equal("native_stage_stale", error.GetProperty("type").GetString());
        Assert.Contains("clawctl setup", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A stage altered after its completion marker was written must not be
    /// trusted because the marker survived.
    /// </summary>
    [Fact]
    public async Task AStageAlteredBehindItsMarkerIsRefusedAndRepairedBySetup()
    {
        HostOptions b = CreatePackage("pkg-b", PackageB());
        Assert.Equal(0, await RunAsync(b, "setup"));
        string root = ReadSetup().AgentNativeRoot!;
        File.Delete(Path.Combine(root, @"node_modules\@openclaw\fs-safe\dist\owner-dacl-batch-worker.js"));

        NativeStageStaleException refusal = await Assert.ThrowsAsync<NativeStageStaleException>(
            () => RunAsync(b, "pwsh", "--command", "exit"));
        Assert.Contains("[incomplete;", refusal.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Runs);

        Assert.Equal(0, await RunAsync(b, "setup"));
        Assert.Equal(0, await RunAsync(b, "pwsh", "--command", "exit"));
        string repaired = Assert.Single(_launcher.Runs).NativeStage!.RootPath!;
        Assert.True(File.Exists(Path.Combine(
            repaired, @"node_modules\@openclaw\fs-safe\dist\owner-dacl-batch-worker.js")));
    }

    private static Dictionary<string, string> PackageA() => new()
    {
        [$"{JavaScriptPackage}/package.json"] = "{\"version\":\"0.25.0\"}",
        [$"{JavaScriptPackage}/dist/index.js"] = "js a",
        [$"{NativePackage}/package.json"] = "{\"version\":\"0.25.0\"}",
        [$"{NativePackage}/fs-safe.win32-x64-msvc.node"] = "native a",
        ["node_modules/lodash/index.js"] = "js only"
    };

    private static Dictionary<string, string> PackageB() => new()
    {
        [$"{JavaScriptPackage}/package.json"] = "{\"version\":\"0.26.5\"}",
        [$"{JavaScriptPackage}/dist/index.js"] = "js b",
        [$"{JavaScriptPackage}/dist/owner-dacl-batch-worker.js"] = "worker b",
        [$"{JavaScriptPackage}/dist/windows-security-helper.exe"] = "helper b",
        [$"{NativePackage}/package.json"] = "{\"version\":\"0.26.5\"}",
        [$"{NativePackage}/fs-safe.win32-x64-msvc.node"] = "native b",
        ["node_modules/lodash/index.js"] = "js only"
    };

    private HostOptions CreatePackage(string name, IReadOnlyDictionary<string, string> files)
    {
        string packageRoot = Path.Combine(_root, name);
        string application = NativeStagePackageFixture.WriteApplication(packageRoot, files);
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string archiveRoot = $"node-v24.20.0-win-{architecture}";
        string archive = Path.Combine(packageRoot, "runtime", $"{archiveRoot}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using StreamWriter node = new(zip.CreateEntry($"{archiveRoot}/node.exe").Open());
            node.Write("fixture node");
        }

        return new HostOptions(application, archive, []);
    }

    private Task<int> RunAsync(HostOptions options, params string[] arguments) =>
        OpenClaw.Launcher.Program.RunControlAsync(
            options,
            arguments,
            _ => { },
            TextWriter.Null,
            TextWriter.Null,
            new ScenarioLifecycle(_runtime),
            readEnvironmentVariable: _ => null,
            getLogonSessionId: () => "fixture-logon");

    private SetupRecord ReadSetup() => _runtime.SetupState.Read(_runtime.ApplicationId).Record!;

    private Task<MxcExecutionResult> ExecuteInGuest(MxcExecutionRequest request)
    {
        (string option, string requestPath) = ParseGuestCommand(request.CommandLine);
        int exitCode = option switch
        {
            "--install-runtime" => SessionRuntimeInstaller.Run(
                requestPath,
                File.ReadAllText,
                File.WriteAllText,
                getLocalApplicationData: () => _agentLocal,
                tryPrependUserPath: _ => false,
                getRuntimeVersion: _ => "24.20.0"),
            "--install-tools" => SessionToolInstaller.Run(requestPath, File.ReadAllText, File.WriteAllText),
            "--inspect" => RespondAsRunningGateway(requestPath),
            "--request" => RunGuestLaunch(request.CommandLine),
            _ when option.StartsWith("--coordinate-state-transfer=", StringComparison.Ordinal) =>
                SessionStateTransfer.Run(
                    requestPath,
                    File.ReadAllText,
                    File.WriteAllText,
                    transfer => new SessionStateTransfer(
                        Directory.CreateDirectory(Path.Combine(_root, "agent-profile")).FullName,
                        Path.Combine(_root, "transfer"),
                        new StateArchiveApplication(transfer))),
            "--companion-config" => SessionCompanionConfig.Run(
                requestPath,
                File.ReadAllText,
                File.WriteAllText,
                profileRoot: Path.Combine(_root, "agent-profile"),
                enterState: new SessionStateAccess(Path.Combine(_root, "state-access")).EnterReader),
            _ => throw new InvalidOperationException($"The scenario does not expect '{option}'.")
        };
        return Task.FromResult(new MxcExecutionResult(exitCode, string.Empty, string.Empty));
    }

    private int RunGuestLaunch(string commandLine)
    {
        (string option, string requestPath) = ParseGuestCommand(commandLine);

        // Production's coordinator only waits on the worker it starts; the
        // worker is the helper mode that admits and launches.
        option = option.Replace("--coordinate-invocation=", "--request-in-job=", StringComparison.Ordinal);
        return GuestProgram.Run(
            [option, requestPath],
            _launcher,
            TextWriter.Null,
            File.ReadAllText,
            GuestProgram.WriteResult);
    }

    private static int RespondAsRunningGateway(string requestPath)
    {
        SessionInspectRequest request = SessionInspectProtocol.ReadRequest(File.ReadAllText(requestPath));
        File.WriteAllText(
            SessionLaunchProtocol.ResultPathFor(requestPath),
            SessionInspectProtocol.SerializeResult(new SessionInspectResult
            {
                RequestId = request.RequestId,
                ProcessFound = true,
                StartTimeMatches = true,
                PortListening = true,
                ListenerOwned = true,
                ListeningPorts = [18789]
            }));
        return 0;
    }

    private static (string Option, string RequestPath) ParseGuestCommand(string commandLine)
    {
        Match match = GuestCommandPattern().Match(commandLine);
        Assert.True(match.Success, commandLine);
        return (match.Groups["option"].Value, match.Groups["request"].Value);
    }

    [GeneratedRegex("^cmd\\.exe /d /s /c \"\"(?<helper>[^\"]+)\" (?<option>\\S+) \"(?<request>[^\"]+)\"\"$")]
    private static partial Regex GuestCommandPattern();

    internal sealed class RecordingLauncher : ISessionProcessLauncher
    {
        public List<SessionLaunchRequest> Runs { get; } = [];

        public List<SessionLaunchRequest> Starts { get; } = [];

        public Func<SessionLaunchRequest, int>? OnRun { get; set; }

        public int Run(SessionLaunchRequest request)
        {
            lock (Runs)
            {
                Runs.Add(request);
            }

            return OnRun?.Invoke(request) ?? 0;
        }

        public SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath)
        {
            Starts.Add(request);
            return new SessionDetachedProcess(4242, DateTimeOffset.UtcNow);
        }
    }

    private sealed class ScenarioLifecycle(SessionRuntime runtime) : IInstallationLifecycle
    {
        public SessionRuntime CreateRuntime(Action<string> log) => runtime;

        public Task EnsureSessionSupportedAsync(Action<string> log, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public PackageRuntimeMetadata ValidatePackageRuntime(HostOptions options, SessionRuntime sessionRuntime) =>
            throw new InvalidOperationException("Fresh setup is not part of this scenario.");

        public ISessionLockHandle AcquireLifecycleLock(SessionRuntime sessionRuntime) =>
            sessionRuntime.AcquireLifecycleLock();

        public Task<TeardownResult> TeardownAsync(
            HostOptions options,
            SessionRuntime sessionRuntime,
            Action<string> log,
            bool lockAlreadyHeld,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Teardown is not part of this scenario.");

        public IInstallationStateCleaner CreateStateCleaner(SessionRuntime sessionRuntime) =>
            throw new InvalidOperationException("Fresh setup is not part of this scenario.");

        public Task<GatewayPersistenceInstallResult> InstallRecoveryAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceInstallResult(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured.",
                Changed: false));

        public Task<GatewayPersistenceStatus> GetRecoveryStatusAsync(
            Action<string> log,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GatewayPersistenceStatus(
                GatewayPersistenceState.Ready,
                GatewayPersistenceLane.TaskScheduler,
                "Logon recovery is configured."));
    }
}
