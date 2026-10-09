using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.Tests.Session;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;
using System.Security.Principal;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class SessionStateTransferTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestCaptureUsesTheRecordedExchangeAndReturnsOnlyMetadata(bool longPath)
    {
        string profile = Path.Combine(_root, "agent");
        Directory.CreateDirectory(profile);
        var application = new ArchiveApplication();
        var backend = new FakeMxcSessionClient();
        string exchange = longPath ? Path.Combine(_root, new string('p', 150)) : Path.Combine(_root, "shared");
        Directory.CreateDirectory(exchange);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var record = new SessionRecord
        {
            SchemaVersion = 1,
            SandboxId = "iso:sandbox1",
            ApplicationId = "PFN:OpenClaw.Gateway_abc123",
            Generation = "fixture-generation",
            WorkspacePath = exchange,
            AgentUserSid = identity.User?.Value
        };
        backend.ExecuteBehavior = _ =>
        {
            string path = Directory.GetFiles(exchange, "state-transfer-*.json", SearchOption.AllDirectories).Single();
            int exitCode = SessionStateTransfer.Run(
                path, File.ReadAllText, File.WriteAllText,
                _ => new SessionStateTransfer(profile, Path.Combine(_root, "transfer"), application));
            return Task.FromResult(new OpenClaw.Launcher.Mxc.MxcExecutionResult(
                exitCode, "output must not be surfaced", "secret stderr"));
        };
        using var operation = new SessionWorkspaceOperation(record, _ => true, privateData: true,
            log: message => throw new InvalidOperationException(message));
        if (longPath)
        {
            Assert.True(operation.WorkspacePath.Length > 260);
        }
        SessionStateTransferResult result = await new SessionExecutor(backend, _ => { })
            .TransferStateAsync(record, @"C:\package\helper.exe", operation,
                Request(SessionStateTransferAction.Capture), CancellationToken.None);

        Assert.Equal(profile, application.Profile);
        Assert.Equal(profile, result.ProfileDirectory);
        Assert.Equal(ArchiveApplication.Bytes, await File.ReadAllBytesAsync(result.Archive!.Path));
        Assert.True(result.Archive.Verified);
        Assert.Equal("state", Assert.Single(result.Assets).Kind);
        Assert.Equal(["execute:iso:sandbox1"], backend.Calls);
        Assert.Empty(Directory.GetFiles(_root, "*.json"));
        Assert.DoesNotContain("credential", SessionStateTransferProtocol.SerializeResult(result),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunDoesNotCreateAnArchive()
    {
        string profile = Path.Combine(_root, "agent");
        Directory.CreateDirectory(profile);
        var application = new ArchiveApplication();
        var transfer = new SessionStateTransfer(profile, Path.Combine(_root, "transfer"), application);

        SessionStateTransferResult result = transfer.Execute(
            Request(SessionStateTransferAction.Capture) with { DryRun = true });

        Assert.Null(result.Archive);
        Assert.Single(result.Assets);
        Assert.Empty(Directory.GetFiles(_root, "*.tar.gz"));
    }

    [Fact]
    public async Task PrivateWorkspaceAllowsGuardedDirectoryAccessButRefusesAChangedObjectPath()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        List<string> log = [];
        var record = new SessionRecord
        {
            WorkspacePath = _root,
            Generation = "private-identity",
            AgentUserSid = identity.User?.Value
        };
        using var operation = new SessionWorkspaceOperation(record, _ => true, privateData: true, log: log.Add);
        string original = operation.WorkspacePath;
        string moved = original + "-moved";
        Directory.Move(original, moved);

        await Assert.ThrowsAsync<SessionException>(() =>
            operation.WriteTextNewAsync(Path.Combine(original, "refused.json"), "{}", CancellationToken.None));

        Assert.Empty(Directory.GetFiles(moved));
    }

    [Theory]
    [InlineData("\"assets\":null")]
    [InlineData("\"assets\":[null]")]
    [InlineData("\"mappings\":null")]
    [InlineData("\"warnings\":[null]")]
    [InlineData("\"schemaVersion\":\"1\"")]
    public void MalformedResultsAreRejectedInsteadOfEscapingValidation(string property)
    {
        string id = Guid.NewGuid().ToString("N");
        string json = $$"""{"kind":"state-transfer","requestId":"{{id}}","schemaVersion":1,{{property}}}""";

        Assert.Throws<SessionLaunchException>(() => SessionStateTransferProtocol.ReadResult(json, id));
    }

    [Fact]
    public void MissingActionAndPathBearingRequestIdsCannotDispatch()
    {
        string json = SessionStateTransferProtocol.SerializeRequest(
            Request(SessionStateTransferAction.Inspect));
        Assert.Throws<SessionLaunchException>(() => SessionStateTransferProtocol.ReadRequest(
            json.Replace("\"action\":\"Inspect\",", string.Empty, StringComparison.Ordinal)));
        Assert.Throws<SessionLaunchException>(() => SessionStateTransferProtocol.ReadRequest(
            SessionStateTransferProtocol.SerializeRequest(
                Request(SessionStateTransferAction.Inspect) with { RequestId = @"..\escape" })));
    }

    private SessionStateTransferRequest Request(SessionStateTransferAction action) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Action = action,
        WorkspaceDirectory = _root,
        NodePath = @"C:\agent\node.exe",
        ApplicationDirectory = @"C:\package\app"
    };

    internal sealed class ArchiveApplication : IStateArchiveApplication
    {
        public static byte[] Bytes => "synthetic credential contents"u8.ToArray();
        public string? Profile { get; private set; }

        public StateArchiveApplicationResult Capture(string profile, string output, bool dryRun)
        {
            Profile = profile;
            if (!dryRun)
            {
                File.WriteAllBytes(output, Bytes);
            }
            return new StateArchiveApplicationResult(output,
                [new StateTransferAsset("state", Path.Combine(profile, ".openclaw"), "payload/state")], []);
        }

        public StateArchiveApplicationResult Extract(string profile, string archive, string destination) =>
            throw new InvalidOperationException("Capture must not extract.");

        public IReadOnlyList<string> RequiredSources(string profile, string previousProfile) =>
            throw new InvalidOperationException("Capture must not read configuration dependencies.");

        public void RebaseConfiguration(string profile, string previousProfile, string currentProfile) =>
            throw new InvalidOperationException("Capture must not change configuration.");
    }
}
