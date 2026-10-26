using System.Security.Cryptography;
using System.IO.Compression;
using System.Security.Principal;
using System.Text.Json;
using OpenClaw.Launcher.Session;
using OpenClaw.Launcher.StateTransfer;
using OpenClaw.Launcher.Tests.StateTransfer;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.AotSmoke;

internal static partial class SmokeProgram
{
    private static Task InvalidRuntimeImageRepairs()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        string archiveRoot = "node-v24.21.0-win-x64";
        string archivePath = Path.Combine(fixture.Root, archiveRoot + ".zip");
        using (ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry(archiveRoot + "/node.exe").Open()))
        {
            writer.Write("synthetic replacement");
        }
        string executable = Path.Combine(fixture.Root, "runtime-data", "OpenClawGatewayMSIX",
            "agent-node", archiveRoot, "node.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "not an executable");
        Directory.CreateDirectory(fixture.ApplicationDirectory);
        string requestPath = Path.Combine(fixture.Root, "runtime-repair.json");
        File.WriteAllText(requestPath, SessionRuntimeProtocol.SerializeRequest(new SessionRuntimeInstallRequest
        {
            RequestId = "native-runtime-repair",
            ArchivePath = archivePath,
            ApplicationDirectory = fixture.ApplicationDirectory,
            UpdateUserPath = false
        }));

        int exitCode = SessionRuntimeInstaller.Run(
            requestPath, File.ReadAllText, File.WriteAllText,
            getLocalApplicationData: () => Path.Combine(fixture.Root, "runtime-data"));

        Assert(exitCode == 0, "Native invalid-image runtime repair failed.");
        Assert(File.ReadAllText(executable) == "synthetic replacement",
            "Native repair did not replace the unlaunchable image from its selected archive.");
        fixture.AssertNoInstallationWorkStarted();
        return Task.CompletedTask;
    }

    private static async Task PrivateStateArchivesAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        string workspace = Path.Combine(fixture.Root, "private-exchange");
        Directory.CreateDirectory(workspace);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ??
            throw new InvalidOperationException("The native fixture user is unavailable.");
        var record = new SessionRecord
        {
            WorkspacePath = workspace,
            Generation = "native-private",
            AgentUserSid = user.Value
        };
        string privateRoot;
        using (var exchange = new SessionWorkspaceOperation(record, _ => true, privateData: true,
            log: message => throw new InvalidOperationException(message)))
        {
            privateRoot = exchange.WorkspacePath;
            Assert(new DirectoryInfo(privateRoot).GetAccessControl().AreAccessRulesProtected,
                "Native state-transfer exchange inherited its parent permissions.");
            byte[] data = "native synthetic archive"u8.ToArray();
            var archive = new StateTransferArchive(
                Path.Combine(privateRoot, "input.tar.gz"), data.Length,
                Convert.ToHexString(SHA256.HashData(data)), Verified: true);
            var store = new StateArchiveStore(Path.Combine(fixture.Root, "private-archives"), _ => { });
            using var input = new MemoryStream(data);
            string retained = await store.PublishAsync(
                input, archive, null, protection: true, CancellationToken.None).ConfigureAwait(false);
            Assert(new FileInfo(retained).GetAccessControl().AreAccessRulesProtected,
                "Native protection archive inherited parent readers.");
            Assert(File.ReadAllBytes(retained).SequenceEqual(data),
                "Native private publication changed archive bytes.");
        }
        Assert(!Directory.Exists(privateRoot), "Native private exchange cleanup did not finish.");
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task StateTransferFailureNamesCommandAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        foreach (string command in new[] { "backup", "restore", "recover" })
        {
            foreach (bool json in new[] { false, true })
            {
                fixture.ClearOutput();
                int exitCode = await fixture.RunAsync(
                    json ? [command, "--json"] : [command],
                    readEnvironment: _ => throw new InvalidOperationException("fixture startup failure"))
                    .ConfigureAwait(false);
                AssertExitCode(1, exitCode, fixture);
                if (json)
                {
                    using JsonDocument document = JsonDocument.Parse(fixture.Output.ToString());
                    Assert(document.RootElement.GetProperty("command").GetString() == command,
                        "Native startup failure lost the selected state-transfer command.");
                    AssertContains(document.RootElement.GetProperty("error").GetProperty("message").GetString()!,
                        "fixture startup failure", fixture);
                    AssertNotContains(fixture.Error.ToString(), "fixture startup failure", fixture);
                }
                else
                {
                    AssertContains(fixture.Error.ToString(), $"clawctl {command}", fixture);
                    AssertContains(fixture.Error.ToString(), "fixture startup failure", fixture);
                    AssertNotContains(fixture.Output.ToString(), "fixture startup failure", fixture);
                }
            }
        }
        fixture.AssertNoInstallationWorkStarted();
    }

    private static async Task StateTransferCommandsAsync()
    {
        using Fixture fixture = await Fixture.CreateWithApplicationAsync().ConfigureAwait(false);
        foreach (string command in new[] { "backup", "restore", "recover" })
        {
            fixture.ClearOutput();
            int help = await fixture.RunAsync([command, "--help"]).ConfigureAwait(false);
            AssertExitCode(0, help, fixture);
            AssertContains(fixture.Output.ToString(), command, fixture);
        }
        string store = Path.Combine(fixture.Root, "archives");
        Directory.CreateDirectory(store);
        string archive = Path.Combine(store, "retained.tar.gz");
        await File.WriteAllTextAsync(archive, "fixture archive").ConfigureAwait(false);
        fixture.ClearOutput();
        int listed = await fixture.RunAsync(["backup", "--list", "--json"]).ConfigureAwait(false);
        AssertExitCode(0, listed, fixture);
        using (JsonDocument document = JsonDocument.Parse(fixture.Output.ToString()))
        {
            Assert(document.RootElement.GetProperty("archives")[0].GetProperty("path").GetString() == archive,
                "Native backup listing did not retain its fixture archive.");
        }
        fixture.ClearOutput();
        int refused = await fixture.RunAsync(["restore", archive, "--json"]).ConfigureAwait(false);
        AssertExitCode(1, refused, fixture);
        using (JsonDocument document = JsonDocument.Parse(fixture.Output.ToString()))
        {
            Assert(!document.RootElement.GetProperty("ok").GetBoolean(),
                "Native restore succeeded without confirmation.");
            AssertContains(document.RootElement.GetProperty("error").GetProperty("message").GetString()!, "--yes", fixture);
        }
        fixture.AssertNoInstallationWorkStarted();
    }

    private static Task StateActivationAndRollback()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "scenarios", $"state-transfer-{Guid.NewGuid():N}");
        string oldProfile = Path.Combine(root, "A1-B2");
        string profile = Path.Combine(root, "C3-D4");
        string workspace = Path.Combine(root, "workspace");
        string directory = Path.Combine(profile, "AppData", "Local", "transfer");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(profile, ".openclaw"));
        Directory.CreateDirectory(Path.Combine(oldProfile, ".openclaw"));
        File.WriteAllText(Path.Combine(profile, ".openclaw", "history.txt"), "current");
        File.WriteAllText(Path.Combine(oldProfile, ".openclaw", "history.txt"), "old");
        File.WriteAllText(Path.Combine(oldProfile, ".openclaw", "openclaw.json"), """{"gateway":{"mode":"local"}}""");
        var application = new ArchiveFixtureApplication();
        string archive = Path.Combine(workspace, "input.tar.gz");
        _ = application.Capture(oldProfile, archive, dryRun: false);
        bool interrupt = true;
        void Move(string source, string target)
        {
            if (Directory.Exists(source))
            {
                Directory.Move(source, target);
            }
            else
            {
                File.Move(source, target);
            }
            if (interrupt && target == Path.Combine(profile, ".openclaw"))
            {
                interrupt = false;
                throw new IOException("fixture interruption");
            }
        }
        var transfer = new SessionStateTransfer(profile, directory, application, Move);
        string transaction = Guid.NewGuid().ToString("N");
        SessionStateTransferRequest Request(SessionStateTransferAction action) => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Action = action,
            WorkspaceDirectory = workspace,
            TransactionId = transaction,
            NodePath = Path.Combine(profile, "node.exe"),
            ApplicationDirectory = Path.Combine(root, "app"),
            ArchivePath = archive,
            ArchiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)))
        };
        try
        {
            SessionStateTransferRequest preparedRequest = Request(SessionStateTransferAction.Prepare);
            SessionStateTransferResult prepared = transfer.Execute(SessionStateTransferProtocol.ReadRequest(
                SessionStateTransferProtocol.SerializeRequest(preparedRequest)));
            Assert(prepared.Pending, "Native preparation did not retain its activation journal.");
            SessionStateTransferRequest captureRequest = Request(SessionStateTransferAction.Capture);
            SessionStateTransferResult protection = transfer.Execute(captureRequest);
            Assert(protection.Archive is not null, "Native current-state capture omitted protection.");
            try
            {
                _ = transfer.Execute(Request(SessionStateTransferAction.Activate) with
                {
                    ProtectionArchive = protection.Archive!.Path,
                    ProtectionSha256 = protection.Archive.Sha256
                });
                throw new InvalidOperationException("Native interrupted activation unexpectedly succeeded.");
            }
            catch (IOException)
            {
                SessionStateTransferResult pending = transfer.Execute(Request(SessionStateTransferAction.Inspect));
                Assert(pending.Pending, "Native interrupted activation lost its journal.");
            }
            SessionStateTransferResult rolledBack = transfer.Execute(Request(SessionStateTransferAction.Rollback));
            Assert(rolledBack.Phase == "rolled-back", "Native rollback did not complete.");
            Assert(File.ReadAllText(Path.Combine(profile, ".openclaw", "history.txt")) == "current",
                "Native rollback lost the prior state.");
            Assert(File.ReadAllText(Path.Combine(oldProfile, ".openclaw", "history.txt")) == "old",
                "Native activation modified the original profile.");
            SessionStateTransferRequest inspectionRequest = Request(SessionStateTransferAction.Inspect);
            SessionStateTransferResult inspection = transfer.Execute(inspectionRequest);
            using var output = new StringWriter();
            ClawCtlJson.WriteResult(output, new StateArchiveCommandResult("restore", 0, inspection));
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            Assert(!document.RootElement.GetProperty("stateTransfer").GetProperty("pending").GetBoolean(),
                "Native JSON reported an activation after rollback.");
            using IDisposable reader = new SessionStateAccess(directory).EnterReader();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return Task.CompletedTask;
    }
}
