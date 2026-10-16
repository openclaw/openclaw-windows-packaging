using System.Security.Cryptography;
using System.Text.Json;
using OpenClaw.Launcher.Tests.StateTransfer;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.AotSmoke;

internal static partial class SmokeProgram
{
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
