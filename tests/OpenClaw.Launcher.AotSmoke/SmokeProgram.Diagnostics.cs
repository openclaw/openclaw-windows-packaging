using System.IO.Compression;
using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.AotSmoke;

internal static partial class SmokeProgram
{
    private static async Task PackageDiagnosticsAreStructuredAsync()
    {
        using Fixture fixture = Fixture.CreateWithoutApplication();
        await WritePackageDiagnosticEvidenceAsync(fixture.Root).ConfigureAwait(false);
    }

    private static async Task WritePackageDiagnosticEvidenceAsync(string root)
    {
        const string inventory = """
            {"packages":[
              {"name":"OpenClawFoundation.OpenClawGateway","version":"2026.11.5.0",
               "packageFullName":"OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
               "packageFamilyName":"OpenClawFoundation.OpenClawGateway_fixture",
               "installLocation":"P:\\WindowsApps\\OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
               "signatureKind":"Store","isDevelopmentMode":false}],
             "commands":[
               {"name":"clawctl.cmd","commandType":"Application","path":"C:\\Users\\alice\\private-bin\\clawctl.cmd"},
               {"name":"clawctl.exe","commandType":"Application","path":"C:\\Users\\alice\\AppData\\Local\\Microsoft\\WindowsApps\\clawctl.exe"}],
             "issues":[],"packagesTruncated":false,"commandsTruncated":false,
             "error":{"message":"synthetic-secret-never-export"}}
            """;
        const string family = "OpenClawFoundation.OpenClawGateway_fixture";
        const string stderr = "synthetic-stderr-secret";
        HostPackageDiagnostics diagnostics = new(
            _ => Task.FromResult(new DiagnosticProcessOutput(
                0, new(inventory, inventory.Length), new(stderr, stderr.Length))),
            () => new DiagnosticCollector(
                family,
                new PackageProvenance(
                    "OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
                    PackageOrigin.Store, false,
                    @"P:\WindowsApps\OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture"),
                $@"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\{family}\clawctl.exe",
                true, "resolved"));
        HostPaths paths = HostPaths.ForRoot(Path.Combine(root, "state"), family);
        SessionRuntime session = SessionRuntime.Create(
            paths, () => throw new InvalidOperationException("No backend may be created."),
            root, _ => { }, new NoDiagnosticBackend());
        GatewayRuntime runtime = GatewayRuntime.Create(
            new HostOptions(null, null, []), paths, session, _ => { });
        string zip = Path.Combine(root, "synthetic-package-diagnostics.zip");
        DiagnosticsBundleResult result = await runtime.CollectLogsAsync(
            zip, null, new DiagnosticProgress(), CancellationToken.None, diagnostics).ConfigureAwait(false);
        Assert(result.BundlePath == zip && !result.SessionReached, "No host-only diagnostics ZIP was produced.");
        using ZipArchive archive = ZipFile.OpenRead(zip);
        using StreamReader reader = new(archive.GetEntry("host/package-diagnostics.json")!.Open());
        string text = await reader.ReadToEndAsync().ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(text);
        Assert(document.RootElement.GetProperty("inventory").GetProperty("commands").GetArrayLength() == 2,
            "Alias inventory did not survive NativeAOT JSON serialization.");
        Assert(document.RootElement.GetProperty("declaredSchemaVersion").GetInt32() == 1,
            "The declared contract did not survive NativeAOT.");
        Assert(!text.Contains("alice", StringComparison.Ordinal) &&
            !text.Contains("synthetic-secret", StringComparison.Ordinal) &&
            !text.Contains("synthetic-stderr-secret", StringComparison.Ordinal),
            "Private fixture data survived structured diagnostic export.");
    }

    private sealed class DiagnosticProgress : IProgress<ClawCtlProgress>
    {
        public void Report(ClawCtlProgress value) { }
    }

    private sealed class NoDiagnosticBackend : IMxcSessionClient
    {
        public Task<MxcProvisionResult> ProvisionAsync(MxcProvisionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostics must not provision.");
        public Task StartAsync(MxcSandboxId sandboxId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No session is recorded.");
        public Task<MxcExecutionResult> ExecuteAsync(MxcSandboxId sandboxId, MxcExecutionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostics must not invoke status.");
        public Task<int> ExecuteAttachedAsync(MxcSandboxId sandboxId, MxcExecutionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostics must not launch commands.");
        public Task StopAsync(MxcSandboxId sandboxId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostics must not stop sessions.");
        public Task DeprovisionAsync(MxcSandboxId sandboxId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostics must not remove sessions.");
    }
}
