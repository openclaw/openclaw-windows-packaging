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
    private readonly List<string> _guestOptions = [];
    private readonly SessionRuntime _runtime;
    private readonly string _agentLocal;
    private (string First, string Second)? _alternatingRoots;
    private int _setupReads;

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
            isInteractiveLogon: () => true,
            readSetupText: ReadSetupText);
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
    /// Restart stops a running gateway only once its replacement would be
    /// admitted, so an update that needs setup leaves the user's gateway
    /// serving instead of stopped until setup.
    /// </summary>
    [Fact]
    public async Task RestartAfterAnUpdateKeepsTheRunningGatewayUntilSetupRestagesIt()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        Assert.Equal(0, await RunAsync(a, "gateway-service", "start"));
        string gatewayBefore = File.ReadAllText(_runtime.Paths.GatewayStatePath);
        HostOptions b = CreatePackage("pkg-b", PackageB());
        using var output = new StringWriter();

        int exitCode = await OpenClaw.Launcher.Program.RunAsync(
            ["gateway-service", "restart", "--json"],
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
        using (JsonDocument document = JsonDocument.Parse(output.ToString()))
        {
            JsonElement error = document.RootElement.GetProperty("error");
            Assert.Equal("native_stage_stale", error.GetProperty("type").GetString());
            Assert.Contains("clawctl setup", error.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("was not stopped", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain("--stop", _guestOptions);
        Assert.Single(_launcher.Starts);
        Assert.Equal(gatewayBefore, File.ReadAllText(_runtime.Paths.GatewayStatePath));

        Assert.Equal(0, await RunAsync(b, "setup"));
        Assert.Equal(0, await RunAsync(b, "gateway-service", "restart"));
        Assert.Contains("--stop", _guestOptions);
        Assert.Equal(ReadSetup().AgentNativeRoot, _launcher.Starts[^1].NativeStage!.RootPath);
    }

    /// <summary>
    /// Repair's backup and Doctor run packaged Node.js, so a stage that would
    /// refuse them is reported before the running gateway is stopped.
    /// </summary>
    [Fact]
    public async Task RepairAfterAnUpdateIsRefusedWithoutStoppingTheGateway()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        Assert.Equal(0, await RunAsync(a, "gateway-service", "start"));
        string gatewayBefore = File.ReadAllText(_runtime.Paths.GatewayStatePath);
        HostOptions b = CreatePackage("pkg-b", PackageB());
        using var output = new StringWriter();

        int exitCode = await OpenClaw.Launcher.Program.RunControlAsync(
            b,
            ["gateway-service", "repair", "--yes", "--json"],
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
        Assert.DoesNotContain("remains stopped", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--stop", _guestOptions);
        Assert.Equal(gatewayBefore, File.ReadAllText(_runtime.Paths.GatewayStatePath));
        Assert.False(Directory.Exists(Path.Combine(_root, "archives")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(_root, "archives")).Any());
    }

    /// <summary>
    /// A launch does not hold the lifecycle lock, so setup can publish
    /// another generation between two reads of its record. The shell's
    /// redirect and the stage the guest admits must still name one root.
    /// </summary>
    [Fact]
    public async Task AShellRedirectsThroughTheStageItIsAdmittedAgainst()
    {
        HostOptions b = CreatePackage("pkg-b", PackageB());
        Assert.Equal(0, await RunAsync(b, "setup"));
        AlternateRecordedRoot();

        Assert.Equal(0, await RunAsync(b, "pwsh", "--command", "exit"));

        SessionLaunchRequest launch = Assert.Single(_launcher.Runs);
        Assert.Equal(
            launch.NativeStage!.RootPath,
            launch.Environment!["OPENCLAW_SHIM_NATIVE_STAGED_ROOT"]);
    }

    [Fact]
    public async Task AForegroundLaunchRedirectsThroughTheStageItIsAdmittedAgainst()
    {
        HostOptions b = CreatePackage("pkg-b", PackageB());
        Assert.Equal(0, await RunAsync(b, "setup"));
        AlternateRecordedRoot();

        await OpenClaw.Launcher.Program.RunAgentAsync(
            b with { OpenClawArguments = ["--version"] },
            _ => { },
            _ => _runtime,
            probeReadiness: _ => Task.FromResult(new MxcReadinessReport(
                "runtime", null, null, MxcHostSupport.Supported, null, MxcSupportEvidence.BackendProbe)),
            getPackageFamilyName: () => _runtime.Paths.PackageFamilyName,
            readEnvironmentVariable: _ => null,
            isInteractive: () => false,
            error: TextWriter.Null,
            getLogonSessionId: () => "fixture-logon",
            errorIsProcessConsoleWriter: () => false,
            errorIsInteractive: () => false,
            supportsUnicode: () => false,
            installationLifecycle: new ScenarioLifecycle(_runtime));

        SessionLaunchRequest launch = Assert.Single(_launcher.Runs);
        Assert.Equal(
            launch.NativeStage!.RootPath,
            launch.Environment![OpenClawRuntimeEnvironment.NativeStagedRootVariable]);
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

    /// <summary>
    /// Repair must not pull the stage out from under a shell that is still
    /// running from it; the next setup after the shell exits reclaims it.
    /// </summary>
    [Fact]
    public async Task AShellStillRunningKeepsItsStageThroughRepairUntilItExits()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        string rootA = ReadSetup().AgentNativeRoot!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _launcher.OnRun = _ =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(30)));
            return 0;
        };
        Task<int> shell = Task.Run(() => RunAsync(a, "pwsh", "--command", "exit"));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        _launcher.OnRun = null;

        HostOptions b = CreatePackage("pkg-b", PackageB());
        Assert.Equal(0, await RunAsync(b, "setup"));
        string rootB = ReadSetup().AgentNativeRoot!;

        Assert.NotEqual(rootA, rootB);
        Assert.Equal(
            "native a",
            File.ReadAllText(Path.Combine(rootA, @"node_modules\@openclaw\fs-safe-win32-x64-msvc\fs-safe.win32-x64-msvc.node")));
        release.Set();
        Assert.Equal(0, await shell.ConfigureAwait(true));

        Assert.Equal(0, await RunAsync(b, "setup"));
        Assert.Equal(rootB, ReadSetup().AgentNativeRoot);
        Assert.False(Directory.Exists(rootA), "The released stage must be reclaimed by the next setup.");
    }

    /// <summary>
    /// A superseded stage whose marker cannot be retired is left for a later
    /// setup; it must not fail the setup that already published its
    /// replacement.
    /// </summary>
    [Fact]
    public async Task ASupersededStageThatCannotBeRetiredDoesNotFailSetup()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        string rootA = ReadSetup().AgentNativeRoot!;
        string markerA = Path.Combine(rootA, ".staged-content-id");
        File.SetAttributes(markerA, FileAttributes.ReadOnly);
        try
        {
            HostOptions b = CreatePackage("pkg-b", PackageB());
            Assert.Equal(0, await RunAsync(b, "setup"));

            SetupRecord setup = ReadSetup();
            Assert.Equal(SetupPhase.Ready, setup.Phase);
            Assert.NotEqual(rootA, setup.AgentNativeRoot);
            Assert.True(File.Exists(markerA), "The unretired stage must be left whole.");
            Assert.Equal(0, await RunAsync(b, "pwsh", "--command", "exit"));
            Assert.Equal(setup.AgentNativeRoot, Assert.Single(_launcher.Runs).NativeStage!.RootPath);
        }
        finally
        {
            File.SetAttributes(markerA, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// A restage that fails leaves the previous stage in place and
    /// remembered, launches stay refused, and a retried setup completes.
    /// </summary>
    [Fact]
    public async Task AFailedRestageKeepsThePreviousStageAndSetupCanBeRetried()
    {
        HostOptions a = CreatePackage("pkg-a", PackageA());
        Assert.Equal(0, await RunAsync(a, "setup"));
        string rootA = ReadSetup().AgentNativeRoot!;
        HostOptions b = CreatePackage("pkg-b", PackageB());
        string locked = Path.Combine(
            b.PackagedApplicationDirectory!,
            @"node_modules\@openclaw\fs-safe\dist\windows-security-helper.exe");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(1, await RunAsync(b, "setup"));
        }

        SetupRecord failed = _runtime.SetupState.Read(_runtime.ApplicationId).Record!;
        Assert.Equal(SetupPhase.Preparing, failed.Phase);
        Assert.Equal(rootA, failed.AgentNativeRoot);
        Assert.Equal([rootA], Directory.GetDirectories(Path.GetDirectoryName(rootA)!));
        SessionException refused = await Assert.ThrowsAsync<SessionException>(
            () => RunAsync(b, "pwsh", "--command", "exit"));
        Assert.Contains("clawctl setup", refused.Message, StringComparison.Ordinal);
        Assert.Empty(_launcher.Runs);

        Assert.Equal(0, await RunAsync(b, "setup"));
        Assert.Equal(0, await RunAsync(b, "pwsh", "--command", "exit"));
        Assert.NotEqual(rootA, Assert.Single(_launcher.Runs).NativeStage!.RootPath);
        Assert.False(Directory.Exists(rootA));
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

    // Publishes a second verified generation with the same content, then
    // makes every later read of the setup record name the other one, as
    // repeated concurrent setups would.
    private void AlternateRecordedRoot()
    {
        string first = ReadSetup().AgentNativeRoot!;
        string second = Path.Combine(Path.GetDirectoryName(first)!, Guid.NewGuid().ToString("N"));
        foreach (string file in Directory.EnumerateFiles(first, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(second, Path.GetRelativePath(first, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        _alternatingRoots = (first, second);
    }

    private string ReadSetupText(string path)
    {
        string text = File.ReadAllText(path);
        if (_alternatingRoots is not (string first, string second))
        {
            return text;
        }

        string recorded = JsonSerializer.Serialize(first)[1..^1];
        string current = JsonSerializer.Serialize(
            Interlocked.Increment(ref _setupReads) % 2 == 0 ? first : second)[1..^1];
        return text.Replace(recorded, current, StringComparison.Ordinal);
    }

    private Task<MxcExecutionResult> ExecuteInGuest(MxcExecutionRequest request)
    {
        (string option, string requestPath) = ParseGuestCommand(request.CommandLine);
        lock (_guestOptions)
        {
            _guestOptions.Add(option);
        }

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
            "--stop" => RespondAsStoppedGateway(requestPath),
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

    private static int RespondAsStoppedGateway(string requestPath)
    {
        SessionInspectRequest request = SessionInspectProtocol.ReadRequest(File.ReadAllText(requestPath));
        File.WriteAllText(
            SessionLaunchProtocol.ResultPathFor(requestPath),
            SessionInspectProtocol.SerializeResult(new SessionInspectResult { RequestId = request.RequestId }));
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
