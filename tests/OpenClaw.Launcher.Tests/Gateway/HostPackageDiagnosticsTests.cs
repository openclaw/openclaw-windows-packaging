using System.ComponentModel;
using System.Text;
using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class HostPackageDiagnosticsTests
{
    internal const string InventoryJson = """
        {
          "packages": [
            {
              "name": "OpenClawFoundation.OpenClawGateway",
              "version": "2026.11.5.0",
              "packageFullName": "OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
              "packageFamilyName": "OpenClawFoundation.OpenClawGateway_fixture",
              "installLocation": "P:\\WindowsApps\\OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
              "signatureKind": "Store",
              "isDevelopmentMode": false,
              "token": "synthetic-package-secret"
            },
            {
              "name": "OpenClaw.Gateway",
              "version": "0.1.2387.18191",
              "packageFullName": "OpenClaw.Gateway_0.1.2387.18191_x64__fixture",
              "packageFamilyName": "OpenClaw.Gateway_fixture",
              "installLocation": "E:\\private-checkout\\artifacts\\local-package\\x64\\layout",
              "signatureKind": "None",
              "isDevelopmentMode": true
            }
          ],
          "commands": [
            {"name":"clawctl.cmd","commandType":"Application","path":"C:\\Users\\alice\\private-bin\\clawctl.cmd"},
            {"name":"clawctl.exe","commandType":"Application","path":"C:\\Users\\alice\\AppData\\Local\\Microsoft\\WindowsApps\\clawctl.exe"}
          ],
          "issues": [],
          "packagesTruncated": false,
          "commandsTruncated": false,
          "error": {"message":"synthetic-nested-secret"},
          "configuration": {"pairing":{"token":"synthetic-pairing-secret"}}
        }
        """;

    internal static DiagnosticCollector Collector() =>
        new("OpenClawFoundation.OpenClawGateway_fixture",
            new PackageProvenance(
                "OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
                PackageOrigin.Store, false,
                @"P:\WindowsApps\OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture"),
            @"C:\Users\alice\AppData\Local\Microsoft\WindowsApps\OpenClawFoundation.OpenClawGateway_fixture\clawctl.exe",
            true, "resolved");

    internal static DiagnosticProcessOutput Output(
        string stdout = InventoryJson, int exitCode = 0, string stderr = "") =>
        new(exitCode, new(stdout, stdout.Length), new(stderr, stderr.Length));

    [Fact]
    public async Task MultiplePackagesAndShadowingAreEvidenceNotANewSelection()
    {
        HostPackageDiagnostics diagnostics = new(
            _ => Task.FromResult(Output(stderr: "synthetic-stderr-secret")), Collector);
        List<string> notes = [];
        using JsonDocument document = JsonDocument.Parse(
            await diagnostics.CollectAsync(notes, CancellationToken.None));

        JsonElement root = document.RootElement;
        JsonElement inventory = root.GetProperty("inventory");
        Assert.Equal(2, inventory.GetProperty("packages").GetArrayLength());
        Assert.Equal("2026.11.5.0", inventory.GetProperty("packages")[0].GetProperty("version").GetString());
        Assert.Equal("Store", inventory.GetProperty("packages")[0].GetProperty("signatureKind").GetString());
        Assert.Equal(
            @"P:\WindowsApps\OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture",
            inventory.GetProperty("packages")[0].GetProperty("installLocation").GetString());
        Assert.Equal("clawctl.cmd", inventory.GetProperty("commands")[0].GetProperty("name").GetString());
        string alias = root.GetProperty("collector").GetProperty("qualifiedControlAlias").GetString()!;
        Assert.Contains(
            @"\WindowsApps\OpenClawFoundation.OpenClawGateway_fixture\clawctl.exe",
            alias, StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\<private-1>\", alias, StringComparison.Ordinal);
        Assert.StartsWith(
            @"C:\Users\<private-1>\",
            inventory.GetProperty("commands")[0].GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal(ClawCtlJson.SchemaVersion, root.GetProperty("declaredSchemaVersion").GetInt32());
        Assert.Equal("status", root.GetProperty("declaredCommand").GetString());
        Assert.Equal("isolated-session", root.GetProperty("declaredIntegration").GetProperty("kind").GetString());
        Assert.StartsWith("not-probed:", root.GetProperty("statusObservation").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("unknown:", root.GetProperty("companionObservation").GetString(), StringComparison.Ordinal);
        Assert.True(root.GetProperty("inventoryProbe").GetProperty("standardErrorPresent").GetBoolean());
        Assert.Single(notes);
        string text = root.GetRawText();
        foreach (string forbidden in new[]
        {
            "alice", "private-checkout", "private-bin", "synthetic-package-secret",
            "synthetic-nested-secret", "synthetic-pairing-secret", "synthetic-stderr-secret"
        })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ClawCtl.CMD")]
    [InlineData("clawctl.com")]
    [InlineData("CLAWCTL.EXE")]
    [InlineData("clawctl.PS1")]
    [InlineData("clawctl.py")]
    public async Task ValidWindowsCommandNamesPreserveInventoryAndOrderedPaths(string name)
    {
        string inventory = InventoryJson.Replace("clawctl.cmd", name, StringComparison.Ordinal);
        HostPackageDiagnostics diagnostics = new(_ => Task.FromResult(Output(inventory)), Collector);
        List<string> notes = [];
        using JsonDocument document = JsonDocument.Parse(
            await diagnostics.CollectAsync(notes, CancellationToken.None));

        JsonElement actual = document.RootElement.GetProperty("inventory");
        Assert.Equal(2, actual.GetProperty("packages").GetArrayLength());
        Assert.Equal(name, actual.GetProperty("commands")[0].GetProperty("name").GetString());
        Assert.EndsWith(
            "\\" + name, actual.GetProperty("commands")[0].GetProperty("path").GetString(), StringComparison.Ordinal);
        Assert.Equal("clawctl.exe", actual.GetProperty("commands")[1].GetProperty("name").GetString());
        Assert.Empty(notes);
    }

    [Theory]
    [InlineData("", 0, "empty-response")]
    [InlineData("plain text synthetic-secret", 0, "invalid-json")]
    [InlineData("{malformed synthetic-secret", 0, "invalid-json")]
    [InlineData("{}", 0, "invalid-json")]
    [InlineData("null", 0, "invalid-inventory")]
    [InlineData("""{"packages":[null],"commands":[],"issues":[],"packagesTruncated":false,"commandsTruncated":false}""",
        0, "invalid-inventory")]
    [InlineData("""{"packages":[],"commands":[],"issues":["synthetic-secret"],"packagesTruncated":false,"commandsTruncated":false}""",
        0, "invalid-inventory")]
    [InlineData("synthetic-secret", 7, "nonzero-exit")]
    public async Task BadInventoryCannotEraseCollectingIdentityOrLeakRawOutput(
        string stdout, int exitCode, string expected)
    {
        HostPackageDiagnostics diagnostics = new(
            _ => Task.FromResult(Output(stdout, exitCode, "synthetic-stderr-secret")), Collector);
        List<string> notes = [];
        string json = await diagnostics.CollectAsync(notes, CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(expected, document.RootElement.GetProperty("inventoryProbe").GetProperty("outcome").GetString());
        Assert.Equal(exitCode, document.RootElement.GetProperty("inventoryProbe").GetProperty("exitCode").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("inventory", out _));
        Assert.True(document.RootElement.GetProperty("collector").GetProperty("qualifiedControlAliasExists").GetBoolean());
        Assert.Single(notes);
        Assert.DoesNotContain("synthetic-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-stderr-secret", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("launch-or-read-failed")]
    public async Task InventoryFailureStillProducesIdentityAndAnExplicitPartialNote(string outcome)
    {
        HostPackageDiagnostics diagnostics = new(
            _ => Task.FromException<DiagnosticProcessOutput>(outcome == "timeout"
                ? new OperationCanceledException()
                : new Win32Exception(5, "denied for alice synthetic-secret")),
            Collector);
        List<string> notes = [];
        string json = await diagnostics.CollectAsync(notes, CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal(outcome, document.RootElement.GetProperty("inventoryProbe").GetProperty("outcome").GetString());
        Assert.True(document.RootElement.GetProperty("collector").GetProperty("qualifiedControlAliasExists").GetBoolean());
        Assert.Single(notes);
        Assert.False(document.RootElement.GetProperty("inventoryProbe").TryGetProperty("standardErrorPresent", out _));
        Assert.DoesNotContain("alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-secret", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PatchedQualifiedAliasKeepsItsPublicNameWhilePrivateLookalikeDirectoriesDoNot()
    {
        DiagnosticCollector patched = Collector() with
        {
            PackageFamilyName = "OpenClawFoundation.OpenClawGateway-proof_fixture",
            QualifiedControlAlias = @"C:\Users\alice\OpenClaw.Gateway-private-project\WindowsApps\OpenClawFoundation.OpenClawGateway-proof_fixture\clawctl-proof.exe"
        };
        HostPackageDiagnostics diagnostics = new(_ => Task.FromResult(Output()), () => patched);
        string json = await diagnostics.CollectAsync([], CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(json);
        string path = document.RootElement.GetProperty("collector").GetProperty("qualifiedControlAlias").GetString()!;

        Assert.EndsWith(
            @"WindowsApps\OpenClawFoundation.OpenClawGateway-proof_fixture\clawctl-proof.exe",
            path, StringComparison.Ordinal);
        Assert.DoesNotContain("private-project", path, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableProvenanceDoesNotHideTheManifestQualifiedAlias()
    {
        string root = TestDirectory.Create();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "AppxManifest.xml"), """
                <Package><Applications><Application Id="Control" Executable="openclaw.exe">
                  <Extensions><Extension Category="windows.appExecutionAlias">
                    <AppExecutionAlias><ExecutionAlias Alias="clawctl-proof.exe"/></AppExecutionAlias>
                  </Extension></Extensions>
                </Application></Applications></Package>
                """);
            DiagnosticCollector identity = HostPackageDiagnostics.ReadCollector(
                HostPaths.ForRoot(Path.Combine(root, "state"), "OpenClawFoundation.OpenClawGateway-proof_fixture"),
                () => throw new Win32Exception(5, "synthetic-private-message"),
                root, @"C:\Users\alice\AppData\Local", _ => false);
            HostPackageDiagnostics diagnostics = new(_ => Task.FromResult(Output()), () => identity);
            List<string> notes = [];
            using JsonDocument document = JsonDocument.Parse(
                await diagnostics.CollectAsync(notes, CancellationToken.None));

            JsonElement collector = document.RootElement.GetProperty("collector");
            Assert.Equal("qualified-alias-missing", collector.GetProperty("aliasStatus").GetString());
            Assert.Equal("Win32Exception", collector.GetProperty("provenanceFailure").GetString());
            Assert.EndsWith(
                @"OpenClawFoundation.OpenClawGateway-proof_fixture\clawctl-proof.exe",
                collector.GetProperty("qualifiedControlAlias").GetString(), StringComparison.Ordinal);
            Assert.Equal(2, notes.Count);
            Assert.DoesNotContain("synthetic-private-message", document.RootElement.GetRawText(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToPartialSuccess()
    {
        using CancellationTokenSource cancellation = new();
        HostPackageDiagnostics diagnostics = new(
            token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<DiagnosticProcessOutput>(token);
            }, Collector);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => diagnostics.CollectAsync([], cancellation.Token));
    }

    [Fact]
    public async Task FifteenSecondDeadlineCancelsTheInventoryWithoutWaitingOnARealTimer()
    {
        ManualDeadline clock = new();
        HostPackageDiagnostics diagnostics = new(
            token =>
            {
                clock.Fire();
                return Task.FromCanceled<DiagnosticProcessOutput>(token);
            }, Collector, clock);
        List<string> notes = [];
        using JsonDocument document = JsonDocument.Parse(
            await diagnostics.CollectAsync(notes, CancellationToken.None));

        Assert.Equal(HostPackageDiagnostics.InventoryTimeout, clock.DueTime);
        Assert.Equal("timeout", document.RootElement.GetProperty("inventoryProbe").GetProperty("outcome").GetString());
        Assert.Single(notes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InboxScriptReportsMockedPackageAndCommandOutcomesWithoutExecutingDiscoveredCommands(
        bool packageFailure, bool commandMissing)
    {
        var start = HostPackageDiagnostics.CreateInventoryStartInfo();
        string script = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1]));
        string boundary = """
            function Import-Module {
                param([string]$Name)
                if ($Name -like '*\Appx\Appx.psd1') { return }
                Microsoft.PowerShell.Core\Import-Module $Name
            }
            function Get-AppxPackage {
                [CmdletBinding()]
                param([string]$Name)
                if (__PACKAGE_FAILURE__) { throw 'synthetic-private-package-error' }
                if ($Name -eq 'OpenClawFoundation.OpenClawGateway*') {
                    [pscustomobject]@{
                        Name='OpenClawFoundation.OpenClawGateway'; Version='2026.11.5.0'
                        PackageFullName='OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture'
                        PackageFamilyName='OpenClawFoundation.OpenClawGateway_fixture'
                        InstallLocation='P:\WindowsApps\OpenClawFoundation.OpenClawGateway_2026.11.5.0_x64__fixture'
                        SignatureKind='Store'; IsDevelopmentMode=$false
                    }
                } elseif ($Name -notin @('OpenClaw.Gateway*','Microsoft.OpenClaw','OpenClawFoundation.OpenClaw','OpenClaw.Tray')) {
                    throw 'Unexpected inventory scope'
                }
            }
            function Get-Command {
                [CmdletBinding()]
                param([string]$Name, [switch]$All, [object]$CommandType)
                if (__COMMAND_MISSING__) {
                    throw [System.Management.Automation.CommandNotFoundException]::new('synthetic-private-command-error')
                }
                if ($Name -ne 'clawctl' -or -not $All) { throw 'Unexpected command discovery' }
                [pscustomobject]@{Name='clawctl.cmd';CommandType='Application';Path='C:\Users\alice\private-bin\clawctl.cmd'}
                [pscustomobject]@{Name='clawctl.exe';CommandType='Application';Path='C:\Users\alice\AppData\Local\Microsoft\WindowsApps\clawctl.exe'}
            }
            """;
        boundary = boundary.Replace("__PACKAGE_FAILURE__", packageFailure ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("__COMMAND_MISSING__", commandMissing ? "$true" : "$false", StringComparison.Ordinal) +
            Environment.NewLine;
        start.ArgumentList[^1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(boundary + script));
        HostPackageDiagnostics diagnostics = new(
            token => HostPackageDiagnostics.RunInventoryAsync(start, token), Collector);
        List<string> notes = [];
        using JsonDocument document = JsonDocument.Parse(
            await diagnostics.CollectAsync(notes, CancellationToken.None));

        Assert.Equal(packageFailure || commandMissing ? 1 : 0, notes.Count);
        JsonElement inventory = document.RootElement.GetProperty("inventory");
        Assert.Equal(packageFailure ? 0 : 1, inventory.GetProperty("packages").GetArrayLength());
        Assert.Equal(commandMissing ? 0 : 2, inventory.GetProperty("commands").GetArrayLength());
        if (packageFailure || commandMissing)
        {
            Assert.Equal(
                packageFailure ? "current-user-package-inventory-failed" : "clean-shell-command-not-found",
                inventory.GetProperty("issues")[0].GetString());
        }
        Assert.DoesNotContain("synthetic-private", document.RootElement.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedStreamsDrainButDoNotRetainTheOverflow()
    {
        string text = new('x', HostPackageDiagnostics.MaximumOutputCharacters + 100);
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        using StreamReader reader = new(stream);
        DiagnosticStreamOutput output = await HostPackageDiagnostics.ReadBoundedAsync(
            reader, CancellationToken.None);

        Assert.Equal(HostPackageDiagnostics.MaximumOutputCharacters, output.Text.Length);
        Assert.Equal(text.Length, output.CharactersRead);
        Assert.True(output.Truncated);
        Assert.Equal(stream.Length, stream.Position);
        HostPackageDiagnostics diagnostics = new(
            _ => Task.FromResult(new DiagnosticProcessOutput(0, output, new("", 0))), Collector);
        List<string> notes = [];
        using JsonDocument document = JsonDocument.Parse(
            await diagnostics.CollectAsync(notes, CancellationToken.None));
        Assert.Equal("output-limit", document.RootElement.GetProperty("inventoryProbe").GetProperty("outcome").GetString());
        Assert.Single(notes);
    }

    [Fact]
    public void InventoryUsesAbsoluteInboxShellWithNoProfileOrShimExecution()
    {
        var start = HostPackageDiagnostics.CreateInventoryStartInfo();

        Assert.Equal(AgentShellResolver.WindowsPowerShellPath, start.FileName);
        Assert.True(Path.IsPathFullyQualified(start.FileName));
        Assert.False(start.UseShellExecute);
        Assert.Contains("-NoProfile", start.ArgumentList);
        Assert.Contains("-NonInteractive", start.ArgumentList);
        Assert.Contains("-EncodedCommand", start.ArgumentList);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
    }

    [Theory]
    [InlineData(@"\\PRIVATE-PC\private-share\Users\alice\clawctl.cmd",
        @"\\<private-1>\<private-2>\Users\<private-3>\clawctl.cmd")]
    [InlineData(@"\\Microsoft\Windows\Users\alice\clawctl.cmd",
        @"\\<private-1>\<private-2>\Users\<private-3>\clawctl.cmd")]
    [InlineData(@"\\?\UNC\Microsoft\Windows\clawctl.cmd",
        @"\\?\UNC\<private-1>\<private-2>\clawctl.cmd")]
    [InlineData(@"\\?\unc\Microsoft\Windows\Users\alice\clawctl.cmd",
        @"\\?\unc\<private-1>\<private-2>\Users\<private-3>\clawctl.cmd")]
    [InlineData(@"C:\Users\S-1-5-21-1-2-3-1001\AppData\Local\clawctl.exe",
        @"C:\Users\<private-1>\AppData\Local\clawctl.exe")]
    [InlineData(@"\\?\P:\Users\alice\AppData\Local\clawctl.exe",
        @"\\?\P:\Users\<private-1>\AppData\Local\clawctl.exe")]
    public void PrivateComponentsNeverSurviveStructuredPathExport(string input, string expected)
    {
        DiagnosticPathRedactor redactor = new();

        Assert.Equal(expected, redactor.Redact(input));
        Assert.Equal(expected, redactor.Redact(input));
    }

    private sealed class ManualDeadline : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new DeadlineTimer();
        }

        public void Fire() => _callback!(_state);

        private sealed class DeadlineTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
