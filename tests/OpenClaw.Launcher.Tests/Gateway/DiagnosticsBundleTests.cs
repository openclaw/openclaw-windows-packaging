using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class DiagnosticsBundleTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    [Fact]
    public async Task CollectionPreservesHostLogsAndRedactsEveryTextSource()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(
            paths.LogPath,
            """gateway failed with {"apiKey":"do-not-share"}""");
        string bundlePath = Path.Combine(_root, "diagnostics.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => candidate.FullName == "host/openclaw.log");
        using var reader = new StreamReader(entry.Open());
        string text = await reader.ReadToEndAsync();
        Assert.Contains(DiagnosticsRedactor.Placeholder, text, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-share", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionReadsALiveLogWithWriteAndDeleteSharing()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        using FileStream liveLog = new(
            paths.LogPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        using (var writer = new StreamWriter(
            liveLog,
            leaveOpen: true))
        {
            writer.Write("still running");
            writer.Flush();
        }

        string bundlePath = Path.Combine(_root, "live.zip");
        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "host/openclaw.log");
    }

    [Fact]
    public async Task CollectionIncludesThePreResetDiagnosticReport()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PreResetReportPath)!);
        await File.WriteAllTextAsync(paths.PreResetReportPath, "sessionSandboxId=fixture");
        string bundlePath = Path.Combine(_root, "pre-reset.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        ZipArchiveEntry entry = Assert.Single(
            archive.Entries,
            candidate => candidate.FullName == "host/pre-reset.log");
        using var reader = new StreamReader(entry.Open());
        Assert.Contains(
            "sessionSandboxId=fixture",
            await reader.ReadToEndAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptSessionStateStillProducesAHostOnlyBundle()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        await File.WriteAllTextAsync(paths.SessionStatePath, "{not-json");
        string bundlePath = Path.Combine(_root, "host-only.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        Assert.False(result.SessionReached);
        Assert.Contains(
            result.Notes,
            note => note.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase));
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "host/openclaw.log");
    }

    // A session that was provisioned but never started has a recorded
    // workspace nothing created. The note must say so, not merely that the
    // helper is unstaged, and the manifest must name the collecting build.
    [Fact]
    public async Task HostOnlyBundleNamesTheUnderlyingCauseAndTheEnvironment()
    {
        (GatewayRuntime runtime, HostPaths paths, SessionRuntime session) =
            CreateRuntime(workspacePath: Path.Combine(_root, "never-created"));
        await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        string bundlePath = Path.Combine(_root, "unstarted.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            "Windows 10.0.26340.9212 (X64 OS, X64 process); fixture",
            new RecordingProgress(),
            CancellationToken.None);

        Assert.False(result.SessionReached);
        string note = Assert.Single(
            result.Notes,
            candidate => candidate.StartsWith(
                "Agent-side logs could not be collected",
                StringComparison.Ordinal));
        Assert.Contains("has not been staged", note, StringComparison.Ordinal);
        Assert.Contains(
            "---> SessionException: The recorded shared workspace could not be opened safely",
            note,
            StringComparison.Ordinal);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        using var manifest = new StreamReader(
            Assert.Single(archive.Entries, entry => entry.FullName == "manifest.txt").Open());
        string manifestText = await manifest.ReadToEndAsync();
        Assert.Contains(
            "Environment: Windows 10.0.26340.9212 (X64 OS, X64 process); fixture",
            manifestText,
            StringComparison.Ordinal);
        Assert.Contains(note, manifestText, StringComparison.Ordinal);

        // No gateway was ever recorded, so nothing is said about its logs.
        Assert.DoesNotContain(
            result.Notes,
            candidate => candidate.StartsWith("Gateway launch logs", StringComparison.Ordinal));
    }

    // A field report's gateway failed twice, and the first failure came from
    // a launch the record no longer named. Every launch's own log and
    // supervisor status must reach the bundle, redacted, while other files in
    // the guest-writable workspace do not.
    [Fact]
    public async Task EveryGatewayLaunchLogAndSupervisorStatusIsCollected()
    {
        string workspace = Path.Combine(_root, "shared");
        Directory.CreateDirectory(workspace);
        (GatewayRuntime runtime, _, SessionRuntime session) = CreateRuntime(workspace);
        await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        string generation = session.Coordinator.GetRecordedStatus().Record!.Generation!;
        string earlier = WriteGatewayLaunch(
            workspace,
            generation,
            "earlier",
            "Gateway failed to start: another OpenClaw process owns gateway-lifecycle\n",
            "the application exited with code 1",
            new DateTime(2026, 9, 24, 16, 28, 23, DateTimeKind.Utc));
        string later = WriteGatewayLaunch(
            workspace,
            generation,
            "later",
            "Control UI: http://127.0.0.1:18789/#token=ghu_verysecretvalue\n",
            "supervising process 4242",
            new DateTime(2026, 9, 24, 16, 29, 45, DateTimeKind.Utc));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, $"gateway-{generation}-later.json"),
            "{}");
        await File.WriteAllTextAsync(
            Path.Combine(workspace, "gateway-another-generation-launch.log"),
            "not this session");
        string bundlePath = Path.Combine(_root, "gateway.zip");
        var progress = new RecordingProgress();

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            progress,
            CancellationToken.None);

        Assert.Equal(bundlePath, result.BundlePath);
        Assert.Contains("Collecting the gateway launch logs.", progress.Messages);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        Assert.Equal(
            [
                $"gateway/{later}.log",
                $"gateway/{later}.status.json",
                $"gateway/{earlier}.log",
                $"gateway/{earlier}.status.json"
            ],
            archive.Entries
                .Select(entry => entry.FullName)
                .Where(name => name.StartsWith("gateway/", StringComparison.Ordinal)));
        Assert.Contains(
            "another OpenClaw process owns gateway-lifecycle",
            await ReadEntryAsync(archive, $"gateway/{earlier}.log"),
            StringComparison.Ordinal);
        Assert.Contains(
            "the application exited with code 1",
            await ReadEntryAsync(archive, $"gateway/{earlier}.status.json"),
            StringComparison.Ordinal);
        string laterLog = await ReadEntryAsync(archive, $"gateway/{later}.log");
        Assert.DoesNotContain("ghu_verysecretvalue", laterLog, StringComparison.Ordinal);
        Assert.Contains(DiagnosticsRedactor.Placeholder, laterLog, StringComparison.Ordinal);
    }

    // The workspace is guest-writable, so neither the number of launches nor
    // a long-running gateway's log may grow a bundle without bound.
    [Fact]
    public async Task OnlyTheNewestLaunchesAndTheEndOfALongLogAreKept()
    {
        string workspace = Path.Combine(_root, "shared");
        Directory.CreateDirectory(workspace);
        (GatewayRuntime runtime, _, SessionRuntime session) = CreateRuntime(workspace);
        await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        string generation = session.Coordinator.GetRecordedStatus().Record!.Generation!;
        var firstLaunch = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        int launches = GatewayRuntime.MaximumGatewayLaunches + 2;
        for (int index = 0; index < launches - 1; index++)
        {
            WriteGatewayLaunch(
                workspace,
                generation,
                $"launch{index:D2}",
                $"launch {index}\n",
                "the application exited with code 1",
                firstLaunch.AddMinutes(index));
        }

        var longLog = new StringBuilder();
        for (int line = 0; longLog.Length <= GatewayRuntime.MaximumGatewayFileBytes * 2; line++)
        {
            longLog.Append(CultureInfo.InvariantCulture, $"line {line:D8} of the running gateway\n");
        }

        longLog.Append("the last line\n");
        string newest = WriteGatewayLaunch(
            workspace,
            generation,
            "newest",
            longLog.ToString(),
            "supervising process 4242",
            firstLaunch.AddHours(1));
        string bundlePath = Path.Combine(_root, "bounded.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        string[] logs =
        [
            .. archive.Entries
                .Select(entry => entry.FullName)
                .Where(name => name.StartsWith("gateway/", StringComparison.Ordinal) &&
                    name.EndsWith(".log", StringComparison.Ordinal))
        ];
        Assert.Equal(GatewayRuntime.MaximumGatewayLaunches, logs.Length);
        Assert.DoesNotContain($"gateway/gateway-{generation}-launch00.log", logs);
        Assert.DoesNotContain($"gateway/gateway-{generation}-launch01.log", logs);
        Assert.Contains("gateway: 2 older launches were not collected; the newest 10 were kept.", result.Notes);
        string kept = await ReadEntryAsync(archive, $"gateway/{newest}.log");
        Assert.True(kept.Length <= GatewayRuntime.MaximumGatewayFileBytes, $"kept {kept.Length} characters");
        Assert.StartsWith("line ", kept, StringComparison.Ordinal);
        Assert.EndsWith("the last line\n", kept, StringComparison.Ordinal);
        Assert.Contains(
            result.Notes,
            note => note.StartsWith(
                $"gateway/{newest}.log: only the last 1024 KiB of ",
                StringComparison.Ordinal));
    }

    // A recorded gateway means the user was told to collect its log, so a
    // workspace that cannot be read must be named rather than skipped.
    [Fact]
    public async Task AnUnreadableWorkspaceForARecordedGatewayIsNamed()
    {
        (GatewayRuntime runtime, _, SessionRuntime session) =
            CreateRuntime(workspacePath: Path.Combine(_root, "never-created"));
        SessionRecord record = await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        session.GatewayState.Write(new GatewayRecord
        {
            SandboxId = record.SandboxId,
            ProcessId = 4242,
            ProcessStartTimeUtc = DateTimeOffset.UnixEpoch
        });
        string bundlePath = Path.Combine(_root, "recorded.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        Assert.Contains(
            result.Notes,
            note => note.StartsWith(
                "Gateway launch logs could not be collected (SessionException: " +
                "The recorded shared workspace could not be opened safely",
                StringComparison.Ordinal));
    }

    // A failure note quotes the failure's full detail. For a malformed MXC
    // response, that detail includes the executor's raw output and
    // diagnostics. Notes reach the manifest and the command's console and
    // JSON result, so they must be redacted like collected file text.
    [Fact]
    public async Task CredentialsInAFailureNoteAreRedactedEverywhereTheNoteGoes()
    {
        string workspace = Path.Combine(_root, "shared");
        Directory.CreateDirectory(workspace);
        var executor = new MalformedStartExecutor(
            workspace,
            malformedOutput: "thread 'main' panicked: Authorization: Bearer ghu_verysecretvalue",
            executorDiagnostics: "fatal: OPENAI_API_KEY=ghu_othersecretvalue");
        (GatewayRuntime runtime, _, SessionRuntime session) = CreateRuntime(
            workspace,
            new MxcCliSessionClient(
                new MxcRuntimeLocation(
                    Path.Combine(_root, "mxc"),
                    Path.Combine(_root, "mxc", "wxc-exec.exe"),
                    Path.Combine(_root, "mxc", "plm.exe"),
                    null),
                executor));
        SessionRecord record = await session.Coordinator.EnsureStartedAsync(CancellationToken.None);
        session.StageHelper(record);
        string bundlePath = Path.Combine(_root, "malformed.zip");

        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            bundlePath,
            environment: null,
            new RecordingProgress(),
            CancellationToken.None);

        string note = Assert.Single(
            result.Notes,
            candidate => candidate.StartsWith(
                "Agent-side logs could not be collected (MxcException:",
                StringComparison.Ordinal));
        Assert.Contains("[code ProtocolViolation]", note, StringComparison.Ordinal);
        Assert.Contains(
            $"Executor diagnostics: fatal: OPENAI_API_KEY={DiagnosticsRedactor.Placeholder}",
            note,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Executor output: thread 'main' panicked: Authorization: Bearer {DiagnosticsRedactor.Placeholder}",
            note,
            StringComparison.Ordinal);
        using ZipArchive archive = ZipFile.OpenRead(bundlePath);
        string manifest = await ReadEntryAsync(archive, "manifest.txt");
        Assert.Contains(note, manifest, StringComparison.Ordinal);
        foreach (string shared in (string[])[manifest, .. result.Notes])
        {
            Assert.DoesNotContain("ghu_verysecretvalue", shared, StringComparison.Ordinal);
            Assert.DoesNotContain("ghu_othersecretvalue", shared, StringComparison.Ordinal);
        }
    }

    private static string WriteGatewayLaunch(
        string workspace,
        string generation,
        string launch,
        string log,
        string supervisorDetail,
        DateTime writtenUtc)
    {
        string stem = $"gateway-{generation}-{launch}";
        string logPath = Path.Combine(workspace, stem + ".log");
        string statusPath = Path.Combine(workspace, stem + ".status.json");
        File.WriteAllText(logPath, log);
        File.WriteAllText(
            statusPath,
            SessionInspectProtocol.SerializeStatus(new SessionSupervisorStatus
            {
                State = SessionSupervisorStatus.ExitedState,
                ProcessId = 4242,
                UpdatedAtUtc = writtenUtc,
                Detail = supervisorDetail
            }));
        File.SetLastWriteTimeUtc(logPath, writtenUtc);
        File.SetLastWriteTimeUtc(statusPath, writtenUtc);
        return stem;
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(
            Assert.Single(archive.Entries, entry => entry.FullName == name).Open());
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    [Fact]
    public void FilenameOnlyOutputResolvesAgainstTheWorkingDirectory()
    {
        HostPaths paths = HostPaths.ForRoot(Path.Combine(_root, "state"));

        string resolved = GatewayRuntime.ResolveBundlePath(
            "diagnostics.zip",
            paths,
            _root);

        Assert.Equal(Path.Combine(_root, "diagnostics.zip"), resolved);
    }

    [Fact]
    public void DefaultOutputUsesTheInjectedCollectionTime()
    {
        HostPaths paths = HostPaths.ForRoot(Path.Combine(_root, "state"));

        string resolved = GatewayRuntime.ResolveBundlePath(
            null,
            paths,
            _root,
            new FixedTimeProvider(
                new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)));

        Assert.Equal(
            Path.Combine(paths.StateRoot, "openclaw-diagnostics-20250102-030405.zip"),
            resolved);
    }

    [Fact]
    public async Task ExplicitExistingOutputNamesThePathAndOutputOption()
    {
        (GatewayRuntime runtime, HostPaths paths, _) = CreateRuntime();
        string bundlePath = Path.Combine(_root, "existing.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(paths.LogPath)!);
        await File.WriteAllTextAsync(paths.LogPath, "host evidence");
        await File.WriteAllTextAsync(bundlePath, "existing");

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => runtime.CollectLogsAsync(bundlePath, environment: null, new RecordingProgress(), CancellationToken.None));

        Assert.Contains(bundlePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("--output", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private (GatewayRuntime Runtime, HostPaths Paths, SessionRuntime Session) CreateRuntime(
        string? workspacePath = null,
        IMxcSessionClient? backend = null)
    {
        HostPaths paths = HostPaths.ForRoot(
            Path.Combine(_root, Guid.NewGuid().ToString("N"), "state"),
            "OpenClaw.Gateway_test");
        string baseDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"), "base");
        string applicationDirectory = Path.Combine(baseDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        File.WriteAllText(Path.Combine(applicationDirectory, "openclaw.mjs"), "fixture");
        string helperPath = SessionRuntime.ResolveHelperPath(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
        File.WriteAllText(helperPath, "fixture");
        if (backend is null)
        {
            var fake = new FakeMxcSessionClient();
            if (workspacePath is not null)
            {
                fake.Metadata = new MxcProvisionMetadata(
                    "agent_1",
                    "S-1-5-21-0-0-0-1001",
                    workspacePath);
            }

            backend = fake;
        }

        SessionRuntime session = SessionRuntime.Create(
            paths,
            () => throw new InvalidOperationException("The test supplies its backend."),
            baseDirectory,
            _ => { },
            backend);
        GatewayRuntime runtime = GatewayRuntime.Create(
            new HostOptions(applicationDirectory, null, []),
            paths,
            session,
            _ => { });
        return (runtime, paths, session);
    }

    /// <summary>
    /// Answers MXC lifecycle phases with fixed envelopes, standing in for the
    /// executor process so the real client and its wire parsing run. The
    /// first start succeeds and every later one prints malformed output.
    /// </summary>
    private sealed class MalformedStartExecutor(
        string workspace,
        string malformedOutput,
        string executorDiagnostics) : IMxcExecutorInvoker
    {
        private int _starts;

        public Task<MxcExecutorOutcome> InvokeAsync(
            MxcExecutorInvocation invocation,
            CancellationToken cancellationToken)
        {
            string phase = MxcWireProtocol.DecodeConfig(invocation.Arguments[1]).Phase!;
            MxcExecutorOutcome outcome = phase switch
            {
                MxcWireProtocol.ProvisionPhase => new MxcExecutorOutcome(
                    0,
                    "{\"result\":{\"sandboxId\":\"iso:fixture\",\"metadata\":{" +
                    "\"agentUserName\":\"agent_1\",\"agentUserSid\":\"S-1-5-21-0-0-0-1001\"," +
                    "\"ephemeralWorkspacePath\":" + JsonSerializer.Serialize(workspace) + "}}}",
                    string.Empty),
                MxcWireProtocol.StartPhase when Interlocked.Increment(ref _starts) == 1 =>
                    new MxcExecutorOutcome(0, """{"result":{}}""", string.Empty),
                MxcWireProtocol.StartPhase =>
                    new MxcExecutorOutcome(1, malformedOutput, executorDiagnostics),
                _ => throw new InvalidOperationException($"Unexpected MXC phase '{phase}'.")
            };
            return Task.FromResult(outcome);
        }
    }
}
