using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.StateTransfer;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;
using StateTransferFiles = OpenClaw.Launcher.StateTransfer.StateTransferFiles;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class StateTransferScenarioTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly ArchiveFixtureApplication _application = new();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly List<string> _log = [];
    private SessionRuntime? _runtime;
    private HostOptions? _options;
    private bool _interruptNextActivation;
    private bool _rejectGatewayStop;
    private bool _changeSnapshot;
    private bool _changePreparedState;
    private bool _holdOriginalAfterMove;
    private FileStream? _cleanupBlocker;
    private Action<SessionStateTransferRequest>? _beforeTransfer;

    private string Current => Path.Combine(_root, "C3-D4");
    private string Old => Path.Combine(_root, "A1-B2");
    private string TransferDirectory => Path.Combine(Current, "AppData", "Local", "OpenClawGatewayMSIX", "state-transfer");
    private string StoreDirectory => Path.Combine(_root, "retained");
    private string GatewayMarker => Path.Combine(_root, "managed-gateway.txt");
    private SessionStateAccess Access => new(TransferDirectory);
    private StateArchiveStore Store => new(StoreDirectory, _log.Add);

    public void Dispose()
    {
        _cleanupBlocker?.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<StateTransferCoordinator> PrepareAsync()
    {
        Directory.CreateDirectory(Current);
        Directory.CreateDirectory(Path.Combine(_root, "workspace"));
        File.WriteAllText(GatewayMarker, "running");
        _backend.Metadata = new MxcProvisionMetadata(
            "C3-D4", "S-1-5-21-0-0-0-1002", Path.Combine(_root, "workspace"));
        _runtime = SessionRuntime.Create(
            HostPaths.ForRoot(Path.Combine(_root, "host"), "OpenClaw.Gateway_abc123"),
            () => throw new InvalidOperationException("The fake isolation boundary must be used."),
            Path.Combine(_root, "package"), _log.Add, _backend);
        Directory.CreateDirectory(Path.GetDirectoryName(_runtime.HelperPath)!);
        File.WriteAllText(_runtime.HelperPath, "fixture helper");
        SessionRecord record = await _runtime.Coordinator.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
        _runtime.CompleteSetup(record, new SessionRuntimeInstallResult
        {
            ExecutablePath = Path.Combine(Current, "node.exe"),
            Version = "24.20.0",
            ArchiveName = "node-v24.20.0-win-x64.zip"
        }, startupEnabled: false);
        _ = _runtime.StageHelper(record);
        _options = new HostOptions(Path.Combine(_root, "package", "app"),
            Path.Combine(_root, "package", "node-v24.20.0-win-x64.zip"), []);
        _backend.Calls.Clear();
        _backend.ExecuteBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(record.WorkspacePath!, "state-transfer-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            SessionStateTransferRequest request = SessionStateTransferProtocol.ReadRequest(File.ReadAllText(requestPath));
            if (_changeSnapshot && request.SourceDirectory is { } source)
            {
                File.AppendAllText(Path.Combine(source, ".openclaw", "history.txt"), "changed");
            }
            if (_changePreparedState && request.Action == SessionStateTransferAction.Activate)
            {
                File.AppendAllText(Path.Combine(TransferDirectory, "transactions", request.TransactionId!,
                    "profile", ".openclaw", "history.txt"), "changed");
            }
            _beforeTransfer?.Invoke(request);
            int exit = SessionStateTransfer.Run(
                requestPath, File.ReadAllText, File.WriteAllText,
                _ => new SessionStateTransfer(Current, TransferDirectory, _application, Move));
            return Task.FromResult(new MxcExecutionResult(exit, "secret stdout", "secret stderr"));
        };
        return new StateTransferCoordinator(_options, () => _runtime, Store, _log.Add,
            stopGateway: (_, _) =>
            {
                if (_rejectGatewayStop)
                {
                    return Task.FromResult(new GatewayStopResult(false, "gateway ownership could not be established",
                        Succeeded: false));
                }
                File.WriteAllText(GatewayMarker, "stopped");
                return Task.FromResult(new GatewayStopResult(true, "stopped"));
            });
    }

    private void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
        if (_holdOriginalAfterMove &&
            source.Equals(Path.Combine(Current, ".openclaw"), StringComparison.OrdinalIgnoreCase))
        {
            _cleanupBlocker = File.Open(Path.Combine(destination, "history.txt"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        if (_interruptNextActivation &&
            destination.Equals(Path.Combine(Current, ".openclaw"), StringComparison.OrdinalIgnoreCase))
        {
            _interruptNextActivation = false;
            throw new IOException("synthetic interruption after the prepared state moved");
        }
    }

    private static void WriteProfile(string profile, string value)
    {
        string state = Path.Combine(profile, ".openclaw");
        string workspace = Path.Combine(profile, "Work");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);
        using (FileStream file = File.Create(Path.Combine(state, "openclaw.json")))
        using (var config = new Utf8JsonWriter(file))
        {
            config.WriteStartObject();
            config.WriteStartObject("gateway");
            config.WriteString("mode", "local");
            config.WriteEndObject();
            config.WriteStartObject("agents");
            config.WriteStartObject("defaults");
            config.WriteString("workspace", workspace);
            config.WriteEndObject();
            config.WriteEndObject();
            config.WriteEndObject();
        }
        File.WriteAllText(Path.Combine(state, "credentials.json"), value + "-synthetic-token");
        File.WriteAllText(Path.Combine(state, "history.txt"), value + "-history");
        File.WriteAllText(Path.Combine(workspace, "notes.txt"), value + "-workspace");
    }

    private string CreateArchive()
    {
        string archive = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tar.gz");
        _ = _application.Capture(Old, archive, dryRun: false);
        return archive;
    }

    private static Dictionary<string, string> Data(string profile) =>
        Directory.GetFiles(Path.Combine(profile, ".openclaw"), "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(profile, "Work"), "*", SearchOption.AllDirectories))
            .ToDictionary(path => Path.GetRelativePath(profile, path), File.ReadAllText, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task RestoreRebasesFullStateProtectsCurrentDataAndPreservesBothIdentities()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> oldData = Data(Old);
        string identity = File.ReadAllText(_runtime!.Paths.SessionStatePath);
        string setup = File.ReadAllText(_runtime.Paths.SetupStatePath);

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("old-history", File.ReadAllText(Path.Combine(Current, ".openclaw", "history.txt")));
        Assert.Equal("old-synthetic-token", File.ReadAllText(Path.Combine(Current, ".openclaw", "credentials.json")));
        Assert.Equal("old-workspace", File.ReadAllText(Path.Combine(Current, "Work", "notes.txt")));
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(Path.Combine(Current, ".openclaw", "openclaw.json")));
        Assert.Equal(Path.Combine(Current, "Work"),
            config.RootElement.GetProperty("agents").GetProperty("defaults").GetProperty("workspace").GetString());
        Assert.Equal(oldData, Data(Old));
        Assert.Equal(identity, File.ReadAllText(_runtime.Paths.SessionStatePath));
        Assert.Equal(setup, File.ReadAllText(_runtime.Paths.SetupStatePath));
        Assert.DoesNotContain("provision", _backend.Calls);
        Assert.Equal("stopped", File.ReadAllText(GatewayMarker));
        Assert.True(result.Transfer!.GatewayStopped);
        Assert.False(result.Transfer.Pending);
        Assert.Equal(SessionConfigReadinessState.StartupEligible, result.Transfer.Readiness!.State);
        string protection = result.Transfer.ProtectionArchive!;
        Assert.True(Assert.Single(Store.List()).Protection);
        string extracted = Path.Combine(_root, "protection-check");
        _ = _application.Extract(Current, protection, extracted);
        Assert.Equal("current-history", File.ReadAllText(Path.Combine(extracted, "snapshot", "payload", "state", "history.txt")));
        Assert.Empty(Directory.GetFiles(_backend.Metadata!.EphemeralWorkspacePath, "*.json"));
        Assert.DoesNotContain("synthetic-token", string.Join('\n', _log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DryRunVerifiesMappingsWithoutStoppingOrReplacingAnything()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), true, false, false), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("preview", result.Transfer!.Phase);
        Assert.Equal(2, result.Transfer.Mappings.Count);
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
        using IDisposable read = Access.EnterReader();
    }

    [Fact]
    public async Task InterruptedMoveRemainsBlockedUntilExplicitRollbackRestoresEveryAsset()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        _interruptNextActivation = true;

        StateArchiveCommandResult failed = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(1, failed.ExitCode);
        Assert.True(failed.Transfer!.Pending);
        Assert.Throws<SessionLaunchException>(Access.EnterReader);
        SessionStateTransferResult inspected = await StateTransferCoordinator.InspectAsync(
            _runtime!, _runtime!.RequireSetup(), CancellationToken.None);
        Assert.True(inspected.Pending);
        Assert.Equal("activating", inspected.Phase);

        StateArchiveCommandResult rollback = await coordinator.RestoreAsync(
            new RestoreOptions(null, false, true, true), CancellationToken.None);

        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, Data(Current));
        Assert.Equal("stopped", File.ReadAllText(GatewayMarker));
        Assert.True(File.Exists(failed.Transfer.ProtectionArchive));
        using IDisposable read = Access.EnterReader();
    }

    [Fact]
    public async Task ChangedPreparedContentsCannotReplaceCurrentStateAndRequireRollback()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        _changePreparedState = true;

        StateArchiveCommandResult failed = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(1, failed.ExitCode);
        Assert.Contains("contents changed", failed.Error, StringComparison.Ordinal);
        Assert.Equal(before, Data(Current));
        Assert.True(failed.Transfer!.Pending);
        Assert.True(File.Exists(failed.Transfer.ProtectionArchive));
        Assert.Throws<SessionLaunchException>(Access.EnterReader);

        StateArchiveCommandResult rollback = await coordinator.RestoreAsync(
            new RestoreOptions(null, false, true, true), CancellationToken.None);

        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, Data(Current));
        using IDisposable read = Access.EnterReader();
    }

    [Fact]
    public async Task CompletedActivationReportsLockedCleanupWithoutClaimingPendingState()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        _holdOriginalAfterMove = true;

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("old-history", File.ReadAllText(Path.Combine(Current, ".openclaw", "history.txt")));
        Assert.Equal("completed", result.Transfer!.Phase);
        Assert.False(result.Transfer.Pending);
        Assert.Contains(result.Transfer.Warnings, warning =>
            warning.Contains("Temporary activation data could not be removed", StringComparison.Ordinal));
        Assert.True(File.Exists(result.Transfer.ProtectionArchive));
        Assert.False(File.Exists(Access.JournalPath));
        SessionStateTransferResult inspected = await StateTransferCoordinator.InspectAsync(
            _runtime!, _runtime!.RequireSetup(), CancellationToken.None);
        Assert.False(inspected.Pending);
        using IDisposable read = Access.EnterReader();
    }

    [Fact]
    public async Task PendingActivationBlocksAttachedDetachedAndCompanionWritersAtTheirEntryPoints()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        _interruptNextActivation = true;
        StateArchiveCommandResult interrupted = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);
        Assert.Equal(1, interrupted.ExitCode);
        Dictionary<string, string> pendingData = Data(Current);
        string marker = Path.Combine(_root, "writer-started.txt");
        SessionLaunchRequest launch = new()
        {
            RequestId = "pending-writer",
            Executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = ["/d", "/c", "echo started>writer-started.txt"],
            WorkingDirectory = _root
        };

        SessionLaunchException attached = Assert.Throws<SessionLaunchException>(
            () => new SessionProcessLauncher(Access.EnterReader).Run(launch));
        Assert.Contains("activation is pending", attached.Message, StringComparison.OrdinalIgnoreCase);
        SessionLaunchRequest detached = launch with
        {
            Mode = SessionLaunchMode.Detached,
            LogPath = Path.Combine(_root, "writer.log"),
            StatusPath = Path.Combine(_root, "writer.status.json")
        };
        string detachedPath = SessionSupervisor.RequestPathFor(detached.StatusPath);
        File.WriteAllText(detachedPath, SessionLaunchProtocol.SerializeRequest(detached));
        int detachedExit = SessionSupervisor.Run(detachedPath, File.ReadAllText, File.WriteAllText, Access.EnterReader);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, detachedExit);
        Assert.Contains("activation is pending",
            SessionInspectProtocol.ReadStatus(File.ReadAllText(detached.StatusPath)).Detail,
            StringComparison.OrdinalIgnoreCase);
        string companionPath = Path.Combine(_root, "companion-request.json");
        File.WriteAllText(companionPath, SessionCompanionConfigProtocol.SerializeRequest(
            new SessionCompanionConfigRequest
            {
                RequestId = "pending-companion",
                Port = 19001,
                NodePath = Path.Combine(Current, "node.exe"),
                ApplicationDirectory = Path.Combine(_root, "package", "app"),
                Environment = new Dictionary<string, string>()
            }));
        int companionExit = SessionCompanionConfig.Run(companionPath, File.ReadAllText, File.WriteAllText,
            profileRoot: Current, enterState: Access.EnterReader);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, companionExit);
        Assert.Contains("activation is pending",
            SessionCompanionConfigProtocol.ReadResult(
                File.ReadAllText(SessionLaunchProtocol.ResultPathFor(companionPath)), "pending-companion").Error,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(pendingData, Data(Current));
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(detached.LogPath));

        StateArchiveCommandResult rollback = await coordinator.RestoreAsync(
            new RestoreOptions(null, false, true, true), CancellationToken.None);

        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(0, new SessionProcessLauncher(Access.EnterReader).Run(launch));
        Assert.True(File.Exists(marker));
        using IDisposable maintenance = Access.EnterMaintenance();
    }

    [Fact]
    public async Task ExistingWriterBlocksProtectionAndNewWritersStayBlockedUntilRollback()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        StateArchiveCommandResult failed;
        using (IDisposable writer = Access.EnterReader())
        {
            failed = await coordinator.RestoreAsync(
                new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);
            Assert.Equal(1, failed.ExitCode);
            Assert.Equal(before, Data(Current));
            Assert.Throws<SessionLaunchException>(Access.EnterReader);
            Assert.True(failed.Transfer!.Pending);
        }
        StateArchiveCommandResult rollback = await coordinator.RestoreAsync(
            new RestoreOptions(null, false, true, true), CancellationToken.None);
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, Data(Current));
    }

    [Fact]
    public async Task FailedGatewayStopCannotActivateAndReleasesPreparedIntent()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        _rejectGatewayStop = true;

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
        using IDisposable read = Access.EnterReader();
    }

    [Fact]
    public async Task ProtectionPublicationFailureCannotActivateAndReleasesPreparedIntent()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        _beforeTransfer = request =>
        {
            if (request.Action == SessionStateTransferAction.Capture)
            {
                Directory.CreateDirectory(StoreDirectory);
                File.WriteAllText(Path.Combine(StoreDirectory, "before-restore"), "blocked directory");
            }
        };

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before, Data(Current));
        Assert.Equal("stopped", File.ReadAllText(GatewayMarker));
        Assert.False(result.Transfer!.Pending);
        Assert.False(File.Exists(Access.JournalPath));
        using IDisposable read = Access.EnterReader();
    }

    [Theory]
    [InlineData("foreign-profile")]
    [InlineData("newer-schema")]
    [InlineData("invalid-transaction")]
    [InlineData("invalid-digest")]
    [InlineData("traversal")]
    [InlineData("protected-root")]
    [InlineData("unrecorded-original")]
    [InlineData("premature-commit")]
    public async Task InvalidJournalBlocksActivationAndAllNewReadersWithoutReplacingData(string corruption)
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        _beforeTransfer = request =>
        {
            if (request.Action != SessionStateTransferAction.Activate)
            {
                return;
            }
            JsonNode journal = JsonNode.Parse(File.ReadAllText(Access.JournalPath))!;
            JsonNode asset = journal["assets"]![0]!;
            switch (corruption)
            {
                case "foreign-profile":
                    journal["profile"] = Old;
                    break;
                case "newer-schema":
                    journal["schemaVersion"] = 2;
                    break;
                case "invalid-transaction":
                    journal["transactionId"] = "not-a-guid";
                    break;
                case "invalid-digest":
                    journal["preparedSha256"] = new string('Z', 64);
                    break;
                case "traversal":
                    asset["relativePath"] = @"..\outside";
                    break;
                case "protected-root":
                    asset["relativePath"] = @"AppData\Local";
                    break;
                case "unrecorded-original":
                    asset["hadOriginal"] = true;
                    break;
                case "premature-commit":
                    journal["phase"] = "committed";
                    break;
            }
            File.WriteAllText(Access.JournalPath, journal.ToJsonString());
        };

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before, Data(Current));
        Assert.True(result.Transfer!.Pending);
        Assert.True(File.Exists(result.Transfer.ProtectionArchive));
        Assert.Throws<SessionLaunchException>(Access.EnterReader);
        await Assert.ThrowsAsync<SessionException>(() => StateTransferCoordinator.InspectAsync(
            _runtime!, _runtime!.RequireSetup(), CancellationToken.None));
    }

    [Fact]
    public async Task RedirectedSourceTreeCannotBeRecoveredOrModifyItsTarget()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        string external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        string evidence = Path.Combine(external, "preserve.txt");
        File.WriteAllText(evidence, "untouched");
        string link = Path.Combine(Old, ".openclaw", "redirected");
        Directory.CreateSymbolicLink(link, external);
        try
        {
            StateArchiveCommandResult result = await coordinator.RecoverAsync(
                new RecoverOptions(Old, null, false, true), CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(before, Data(Current));
            Assert.Equal("untouched", File.ReadAllText(evidence));
            Assert.Equal("running", File.ReadAllText(GatewayMarker));
            Assert.Empty(Store.List());
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task RedirectedPreparedTreeCannotActivateOrModifyItsExternalTarget()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        string external = Path.Combine(_root, "external");
        Directory.CreateDirectory(external);
        string evidence = Path.Combine(external, "preserve.txt");
        File.WriteAllText(evidence, "untouched");
        string? link = null;
        _beforeTransfer = request =>
        {
            if (request.Action == SessionStateTransferAction.Activate)
            {
                link = Path.Combine(TransferDirectory, "transactions", request.TransactionId!,
                    "profile", ".openclaw", "redirected");
                Directory.CreateSymbolicLink(link, external);
            }
        };

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(before, Data(Current));
        Assert.Equal("untouched", File.ReadAllText(evidence));
        Assert.True(result.Transfer!.Pending);
        Assert.Throws<SessionLaunchException>(Access.EnterReader);
        Assert.NotNull(link);
        Directory.Delete(link);
        StateArchiveCommandResult rollback = await coordinator.RestoreAsync(
            new RestoreOptions(null, false, true, true), CancellationToken.None);
        Assert.Equal(0, rollback.ExitCode);
        Assert.Equal(before, Data(Current));
    }

    [Theory]
    [InlineData("external", "unsafe")]
    [InlineData("protected-root", "replace Windows")]
    [InlineData("case-collision", "colliding")]
    [InlineData("escaped-payload", "unsafe")]
    public async Task UnsafeArchiveMappingsAreRejectedBeforeGatewayStop(string corruption, string expectedError)
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        string archive = CreateArchive();
        StateTransferAsset state = new("state", Path.Combine(Old, ".openclaw"), "snapshot/payload/state");
        StateTransferAsset workspace = new("workspace", Path.Combine(Old, "Work"), "snapshot/payload/workspace");
        _application.ExtractionAssets = corruption switch
        {
            "external" => [state, workspace with { SourcePath = Path.Combine(_root, "external") }],
            "protected-root" => [state, workspace with { SourcePath = Path.Combine(Old, "AppData", "Local") }],
            "case-collision" => [state, state with { SourcePath = Path.Combine(Old, ".OPENCLAW") }, workspace],
            "escaped-payload" => [state with { ArchivePath = @"..\escaped" }, workspace],
            _ => throw new InvalidOperationException("Unknown archive fixture corruption.")
        };

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(archive, false, true, false), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
        Assert.False(File.Exists(Access.JournalPath));
        using IDisposable reader = Access.EnterReader();
    }

    [Fact]
    public async Task CancellationAfterPreparationPreservesCurrentDataAndReleasesIntent()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        using var cancellation = new CancellationTokenSource();
        _beforeTransfer = request =>
        {
            if (request.Action == SessionStateTransferAction.Capture)
            {
                cancellation.Cancel();
            }
        };

        StateArchiveCommandResult result = await coordinator.RestoreAsync(
            new RestoreOptions(CreateArchive(), false, true, false), cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("cancelled", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, Data(Current));
        Assert.False(result.Transfer!.Pending);
        Assert.False(File.Exists(Access.JournalPath));
        Assert.Empty(Store.List());
        using IDisposable reader = Access.EnterReader();
    }

    [Fact]
    public async Task FilesystemRootCannotBeUsedAsARecoveryProfile()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        Dictionary<string, string> before = Data(Current);

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Path.GetPathRoot(_root)!, null, false, true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("filesystem root", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task RecoverNormalizesAReadOnlySnapshotThenUsesTheSameRestorePath()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        File.WriteAllText(Path.Combine(Old, ".openclaw", "state.sqlite"), "synthetic base database");
        File.WriteAllText(Path.Combine(Old, ".openclaw", "state.sqlite-wal"), "synthetic committed WAL");
        Dictionary<string, string> before = Data(Old);
        string identity = File.ReadAllText(_runtime!.Paths.SessionStatePath);

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(before, Data(Old));
        Assert.Equal("old-history", File.ReadAllText(Path.Combine(Current, ".openclaw", "history.txt")));
        Assert.Equal("old-workspace", File.ReadAllText(Path.Combine(Current, "Work", "notes.txt")));
        Assert.Equal("synthetic committed WAL", Encoding.UTF8.GetString(_application.CapturedWal!));
        Assert.DoesNotContain(Old, _application.CapturedProfiles);
        Assert.Equal(identity, File.ReadAllText(_runtime.Paths.SessionStatePath));
        Assert.Equal(2, Store.List().Count);
        Assert.True(File.Exists(result.Transfer!.Archive!.Path));
        Assert.Equal(result.Transfer.Archive.Path, Store.Select(null));
        Assert.Equal("stopped", File.ReadAllText(GatewayMarker));
        Assert.DoesNotContain("synthetic-token",
            SessionStateTransferProtocol.SerializeResult(result.Transfer), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecoveryHoldsSourceFilesAndProfileIdentityUntilActivationCompletes()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        string digest = StateTransferFiles.Digest(Old);
        _beforeTransfer = request =>
        {
            if (request.Action == SessionStateTransferAction.Activate)
            {
                Assert.Throws<IOException>(() => File.AppendAllText(Path.Combine(Old, ".openclaw", "history.txt"), "edit"));
                Assert.Throws<IOException>(() => Directory.Move(Old, Old + "-moved"));
            }
        };

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(digest, StateTransferFiles.Digest(Old));
        Assert.Equal("old-history", File.ReadAllText(Path.Combine(Current, ".openclaw", "history.txt")));
    }

    [Fact]
    public async Task ChangedSourceInventoryBlocksRecoveryWithoutUndoingTheExternalEdit()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        string addition = Path.Combine(Old, ".openclaw", "concurrent.txt");
        _beforeTransfer = request =>
        {
            if (request.Action == SessionStateTransferAction.Recover && !File.Exists(addition))
            {
                File.WriteAllText(addition, "external edit");
            }
        };

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("inventory changed", result.Error, StringComparison.Ordinal);
        Assert.Equal("external edit", File.ReadAllText(addition));
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task CompletedRecoveryReportsLockedSnapshotCleanupAndKeepsBothRetainedArchives()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Old);
        _beforeTransfer = request =>
        {
            if (request.Action == SessionStateTransferAction.Activate)
            {
                string snapshot = Directory.GetDirectories(
                    _backend.Metadata!.EphemeralWorkspacePath, "state-source-*").Single();
                _cleanupBlocker = File.Open(Path.Combine(snapshot, ".openclaw", "history.txt"),
                    FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        };

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("old-history", File.ReadAllText(Path.Combine(Current, ".openclaw", "history.txt")));
        Assert.Equal(before, Data(Old));
        Assert.Equal("completed", result.Transfer!.Phase);
        Assert.False(result.Transfer.Pending);
        Assert.Contains(result.Transfer.Warnings, warning =>
            warning.Contains("Temporary recovery snapshot could not be removed", StringComparison.Ordinal));
        Assert.True(File.Exists(result.Transfer.Archive!.Path));
        Assert.True(File.Exists(result.Transfer.ProtectionArchive));
        Assert.Equal(2, Store.List().Count);
        using IDisposable reader = Access.EnterReader();
    }

    [Fact]
    public async Task RecoveryPreservesCommittedWalRowsAndNormalizesOnlyItsPrivateSnapshot()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        string sourceDatabase = Path.Combine(Old, ".openclaw", "state.sqlite");
        const string createDatabase = """
            import { DatabaseSync } from "node:sqlite";
            const database = new DatabaseSync(process.argv[1]);
            database.exec(`
              PRAGMA journal_mode=WAL;
              CREATE TABLE evidence (id INTEGER PRIMARY KEY, message TEXT NOT NULL);
              INSERT INTO evidence(message) VALUES ('baseline');
              PRAGMA wal_checkpoint(TRUNCATE);
              PRAGMA wal_autocheckpoint=0;
              INSERT INTO evidence(message) VALUES ('wal-commit');
            `);
            console.log("committed");
            process.stdin.resume();
            """;
        using (Process writer = StateTransferProcessFixture.StartNode(
            StateTransferProcessFixture.Request(_root, createDatabase, sourceDatabase)))
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                Assert.Equal("committed", await writer.StandardOutput.ReadLineAsync(budget.Token));
            }
            finally
            {
                StateTransferProcessFixture.Stop(writer);
            }
        }
        Assert.True(new FileInfo(sourceDatabase + "-wal").Length > 0);
        string sourceDigest = StateTransferFiles.Digest(Old);
        string baseOnly = Path.Combine(_root, "base-only.sqlite");
        File.Copy(sourceDatabase, baseOnly);
        Assert.Equal(["baseline"], ReadSqlite(baseOnly));
        string[]? normalizedRows = null;
        string? normalizedProfile = null;
        _application.BeforeCapture = profile =>
        {
            string database = Path.Combine(profile, ".openclaw", "state.sqlite");
            if (File.Exists(database))
            {
                normalizedProfile = profile;
                normalizedRows = ReadSqlite(database);
                Assert.False(File.Exists(database + "-wal"));
            }
        };

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(normalizedRows);
        Assert.Equal(["baseline", "wal-commit"], normalizedRows);
        Assert.NotEqual(Old, normalizedProfile);
        Assert.Equal(sourceDigest, StateTransferFiles.Digest(Old));
        Assert.Equal(["baseline", "wal-commit"], ReadSqlite(Path.Combine(Current, ".openclaw", "state.sqlite")));
        Assert.True(File.Exists(sourceDatabase + "-wal"));
        Assert.True(File.Exists(result.Transfer!.Archive!.Path));
        Assert.True(File.Exists(result.Transfer.ProtectionArchive));
    }

    private string[] ReadSqlite(string path)
    {
        const string readDatabase = """
            import { DatabaseSync } from "node:sqlite";
            const database = new DatabaseSync(process.argv[1]);
            const rows = database.prepare("SELECT message FROM evidence ORDER BY id").all();
            database.close();
            process.stdout.write(JSON.stringify(rows.map((row) => row.message)));
            """;
        SessionProcessOutput result = SessionProcessLauncher.Capture(
            StateTransferProcessFixture.Request(_root, readDatabase, path), TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode);
        using JsonDocument rows = JsonDocument.Parse(result.StandardOutput);
        return [.. rows.RootElement.EnumerateArray().Select(row => row.GetString()!)];
    }

    [Fact]
    public async Task RecoveryDryRunFindsContainedWorkspaceWithoutPublishingOrActivating()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, true, false), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.Transfer!.Archive);
        Assert.Contains(Path.Combine(Old, "Work"), result.Transfer.RequiredSources);
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task LockedOfflineSourceCannotBeCapturedOrStopTheGateway()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        using FileStream writer = new(Path.Combine(Old, ".openclaw", "history.txt"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Dictionary<string, string> before = Data(Current);

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("still in use", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, Data(Current));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Empty(Store.List());
    }

    [Fact]
    public async Task ExternalDependenciesAndChangedSnapshotsBlockAutomaticRecovery()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> before = Data(Current);
        _application.AdditionalDependencies = [Path.Combine(_root, "external", "agent")];

        StateArchiveCommandResult external = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);
        Assert.Equal(1, external.ExitCode);
        Assert.Equal(before, Data(Current));
        Assert.Empty(Store.List());

        _application.AdditionalDependencies = [];
        _changeSnapshot = true;
        StateArchiveCommandResult changed = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), CancellationToken.None);
        Assert.Equal(1, changed.ExitCode);
        Assert.Contains("snapshot changed", changed.Error, StringComparison.Ordinal);
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
        Assert.Equal(before, Data(Current));
    }

    [Fact]
    public async Task CurrentProfileAndSourceContainedDestinationsAreRefused()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        StateArchiveCommandResult same = await coordinator.RecoverAsync(
            new RecoverOptions(Current, null, false, true), CancellationToken.None);
        StateArchiveCommandResult inside = await coordinator.RecoverAsync(
            new RecoverOptions(Old, Path.Combine(Old, "recovery.tar.gz"), false, true), CancellationToken.None);

        Assert.Equal(1, same.ExitCode);
        Assert.Equal(1, inside.ExitCode);
        Assert.Empty(Store.List());
        Assert.False(File.Exists(Path.Combine(Old, "recovery.tar.gz")));
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
    }

    [Fact]
    public async Task CancelledRecoveryLeavesBothProfilesAndNoPublishedArchive()
    {
        StateTransferCoordinator coordinator = await PrepareAsync();
        WriteProfile(Current, "current");
        WriteProfile(Old, "old");
        Dictionary<string, string> current = Data(Current);
        Dictionary<string, string> old = Data(Old);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        StateArchiveCommandResult result = await coordinator.RecoverAsync(
            new RecoverOptions(Old, null, false, true), cancellation.Token);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(current, Data(Current));
        Assert.Equal(old, Data(Old));
        Assert.Empty(Store.List());
        Assert.Equal("running", File.ReadAllText(GatewayMarker));
    }
}
