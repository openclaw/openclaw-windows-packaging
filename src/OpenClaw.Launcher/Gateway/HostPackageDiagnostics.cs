using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

internal sealed record DiagnosticPackage(
    string Name,
    string Version,
    string PackageFullName,
    string PackageFamilyName,
    string InstallLocation,
    string SignatureKind,
    bool IsDevelopmentMode);

internal sealed record DiagnosticCommand(string Name, string CommandType, string Path);

internal sealed record DiagnosticInventory(
    DiagnosticPackage[] Packages,
    DiagnosticCommand[] Commands,
    string[] Issues,
    bool PackagesTruncated,
    bool CommandsTruncated);

internal sealed record DiagnosticCollector(
    string? PackageFamilyName,
    PackageProvenance? Provenance,
    string? QualifiedControlAlias,
    bool? QualifiedControlAliasExists,
    string AliasStatus,
    string? ProvenanceFailure = null);

internal sealed record DiagnosticInventoryProbe(
    string Outcome,
    int? ExitCode = null,
    bool? StandardErrorPresent = null,
    bool? OutputTruncated = null,
    long? StandardOutputCharacters = null,
    long? StandardErrorCharacters = null,
    string? FailureType = null,
    int? NativeErrorCode = null,
    long? JsonLine = null,
    long? JsonByte = null);

internal sealed record HostPackageDiagnosticDocument(
    DateTimeOffset CollectedAtUtc,
    string Source,
    DiagnosticCollector Collector,
    DiagnosticInventoryProbe InventoryProbe,
    DiagnosticInventory? Inventory,
    int DeclaredSchemaVersion,
    string DeclaredCommand,
    ClawCtlJsonIntegration DeclaredIntegration,
    string StatusObservation,
    string CompanionObservation);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectRequiredConstructorParameters = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HostPackageDiagnosticDocument))]
[JsonSerializable(typeof(DiagnosticInventory))]
internal sealed partial class HostPackageDiagnosticJsonContext : JsonSerializerContext;

/// <summary>Read-only, current-user evidence independent of the isolated session.</summary>
internal sealed class HostPackageDiagnostics(
    Func<CancellationToken, Task<DiagnosticProcessOutput>> inventory,
    Func<DiagnosticCollector> collector,
    TimeProvider? clock = null)
{
    internal const int MaximumOutputCharacters = 256 * 1024;
    internal const int MaximumInventoryEntries = 32;
    internal static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(15);

    public static HostPackageDiagnostics CreateDefault(HostPaths paths) =>
        new(RunInventoryAsync, () => ReadCollector(paths));

    public async Task<string> CollectAsync(List<string> notes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticCollector identity;
        try
        {
            identity = collector();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or
            EntryPointNotFoundException or DllNotFoundException)
        {
            identity = new(null, null, null, null, $"identity-unavailable: {exception.GetType().Name}");
        }

        DiagnosticInventory? items = null;
        DiagnosticInventoryProbe probe;
        using CancellationTokenSource timeout = new(InventoryTimeout, clock ?? TimeProvider.System);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            DiagnosticProcessOutput output = await inventory(linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            probe = new(
                output.ExitCode == 0 ? "collected" : "nonzero-exit",
                output.ExitCode,
                output.StandardError.CharactersRead > 0,
                output.StandardOutput.Truncated || output.StandardError.Truncated,
                output.StandardOutput.CharactersRead,
                output.StandardError.CharactersRead);
            if (output.ExitCode == 0 && !output.StandardOutput.Truncated)
            {
                try
                {
                    items = JsonSerializer.Deserialize(
                        output.StandardOutput.Text,
                        HostPackageDiagnosticJsonContext.Default.DiagnosticInventory);
                    if (!IsValid(items))
                    {
                        items = null;
                        probe = probe with { Outcome = "invalid-inventory" };
                    }
                }
                catch (JsonException exception)
                {
                    probe = probe with
                    {
                        Outcome = string.IsNullOrWhiteSpace(output.StandardOutput.Text)
                            ? "empty-response"
                            : "invalid-json",
                        JsonLine = exception.LineNumber,
                        JsonByte = exception.BytePositionInLine
                    };
                }
            }
            else if (output.StandardOutput.Truncated)
            {
                probe = probe with { Outcome = "output-limit" };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            probe = new("timeout");
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidOperationException)
        {
            probe = new("launch-or-read-failed",
                FailureType: exception.GetType().Name,
                NativeErrorCode: exception is Win32Exception native ? native.NativeErrorCode : null);
        }

        if (probe.Outcome != "collected" || probe.StandardErrorPresent == true ||
            items is { Issues.Length: > 0 } or { PackagesTruncated: true } or { CommandsTruncated: true })
        {
            notes.Add(
                $"Host package/alias inventory is partial ({probe.Outcome}); " +
                "see host/package-diagnostics.json. Raw process output was not included.");
        }
        if (identity.AliasStatus != "resolved")
        {
            notes.Add(
                $"Collecting package control alias: {identity.AliasStatus}; " +
                "see host/package-diagnostics.json.");
        }
        if (identity.ProvenanceFailure is not null)
        {
            notes.Add(
                "Collecting package provenance was unavailable; see host/package-diagnostics.json.");
        }

        List<string> publicParts =
        [
            .. items?.Packages.SelectMany(package => new[]
            {
                package.Name, package.PackageFamilyName, package.PackageFullName
            }) ?? []
        ];
        if (identity.PackageFamilyName is string family)
        {
            publicParts.Add(family);
        }
        if (identity.Provenance is { } ownPackage)
        {
            publicParts.Add(ownPackage.FullName);
        }
        if (identity.QualifiedControlAlias is string ownAlias)
        {
            publicParts.Add(Path.GetFileName(ownAlias));
        }
        DiagnosticPathRedactor redactor = new(publicParts);
        identity = identity with
        {
            Provenance = identity.Provenance is { } provenance
                ? provenance with { InstallPath = redactor.Redact(provenance.InstallPath) }
                : null,
            QualifiedControlAlias = redactor.Redact(identity.QualifiedControlAlias)
        };
        if (items is not null)
        {
            items = items with
            {
                Packages = [.. items.Packages.Select(package => package with
                {
                    InstallLocation = redactor.Redact(package.InstallLocation)!
                })],
                Commands = [.. items.Commands.Select(command => command with
                {
                    Path = redactor.Redact(command.Path)!
                })]
            };
        }

        return JsonSerializer.Serialize(
            new HostPackageDiagnosticDocument(
                (clock ?? TimeProvider.System).GetUtcNow(),
                "collecting-process identity; current-user AppX inventory; inbox Windows PowerShell -NoProfile Get-Command clawctl -All",
                identity,
                probe,
                items,
                ClawCtlJson.SchemaVersion,
                ClawCtlCommandLine.StatusCommandName,
                ClawCtlJson.Integration,
                "not-probed: status can start the recorded session; declared integration is a build fact, not an observed response",
                "unknown: installed Companion inventory is current metadata, not its selected Gateway, active distribution, or historical failure"),
            HostPackageDiagnosticJsonContext.Default.HostPackageDiagnosticDocument);
    }

    private static bool IsValid(DiagnosticInventory? items) =>
        items is not null &&
        items.Packages is not null && items.Commands is not null && items.Issues is not null &&
        items.Packages.Length <= MaximumInventoryEntries &&
        items.Commands.Length <= MaximumInventoryEntries &&
        items.Issues.All(issue => issue is
            "current-user-package-inventory-failed" or "clean-shell-command-resolution-failed" or
            "clean-shell-command-not-found") &&
        items.Packages.All(package =>
            package is not null &&
            IsPublicIdentity(package.Name) &&
            IsPublicIdentity(package.PackageFamilyName) &&
            IsPublicIdentity(package.PackageFullName) &&
            Version.TryParse(package.Version, out _) &&
            package.InstallLocation is not null &&
            package.SignatureKind is "None" or "Developer" or "Enterprise" or "Store" or "System") &&
        items.Commands.All(command =>
            command is not null &&
            command.Name is "clawctl" or "clawctl.exe" or "clawctl.cmd" or "clawctl.bat" or "clawctl.ps1" &&
            command.CommandType is "Application" or "ExternalScript" &&
            command.Path is not null);

    private static bool IsPublicIdentity(string? value) =>
        value is { Length: > 0 and <= 256 } &&
        (value.StartsWith("OpenClawFoundation.OpenClaw", StringComparison.Ordinal) ||
            value.StartsWith("OpenClaw.Gateway", StringComparison.Ordinal) ||
            value.StartsWith("Microsoft.OpenClaw", StringComparison.Ordinal) ||
            value.StartsWith("OpenClaw.Tray", StringComparison.Ordinal)) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static DiagnosticCollector ReadCollector(HostPaths paths) =>
        ReadCollector(paths, PackageIdentity.TryReadProvenance, AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), File.Exists);

    internal static DiagnosticCollector ReadCollector(
        HostPaths paths,
        Func<PackageProvenance?> readProvenance,
        string packageDirectory,
        string localApplicationData,
        Func<string, bool> fileExists)
    {
        PackageProvenance? provenance = null;
        string? failure = null;
        try
        {
            provenance = readProvenance();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or
            EntryPointNotFoundException or DllNotFoundException)
        {
            failure = exception.GetType().Name;
        }
        if (paths.PackageFamilyName is not string family)
        {
            return new(null, provenance, null, null, "unpackaged", failure);
        }
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            return new(family, provenance, null, null, "local-application-data-unavailable", failure);
        }
        if (!PackageExecutionAlias.TryResolve(
            packageDirectory, family, "Control", "openclaw.exe", localApplicationData,
            out string? alias, out _))
        {
            return new(family, provenance, null, null, "manifest-or-control-alias-unavailable", failure);
        }
        bool exists = fileExists(alias);
        return new(family, provenance, alias, exists, exists ? "resolved" : "qualified-alias-missing", failure);
    }

    internal static ProcessStartInfo CreateInventoryStartInfo()
    {
        ProcessStartInfo start = new(AgentShellResolver.WindowsPowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(AgentShellResolver.WindowsPowerShellPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[]
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(InventoryScript))
        })
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static Task<DiagnosticProcessOutput> RunInventoryAsync(CancellationToken cancellationToken) =>
        RunInventoryAsync(CreateInventoryStartInfo(), cancellationToken);

    internal static async Task<DiagnosticProcessOutput> RunInventoryAsync(
        ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = new() { StartInfo = startInfo };
        process.Start();
        Task<DiagnosticStreamOutput> stdout = ReadBoundedAsync(process.StandardOutput, cancellationToken);
        Task<DiagnosticStreamOutput> stderr = ReadBoundedAsync(process.StandardError, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new(process.ExitCode,
                await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // Observe both readers even on cancellation; neither pipe may keep collection alive.
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    internal static async Task<DiagnosticStreamOutput> ReadBoundedAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder text = new();
        char[] buffer = new char[4096];
        long total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            int keep = Math.Min(read, MaximumOutputCharacters - text.Length);
            text.Append(buffer, 0, keep);
        }
        return new(text.ToString(), total);
    }

    // Explicit inbox imports avoid user modules, profiles, and PATH shims. No discovered alias is executed.
    private const string InventoryScript = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $PSModuleAutoLoadingPreference = 'None'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        Import-Module "$PSHOME\Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1"
        $packages = @()
        $commands = @()
        $issues = @()
        try {
            Import-Module "$PSHOME\Modules\Appx\Appx.psd1"
            foreach ($name in @('OpenClawFoundation.OpenClawGateway*', 'OpenClaw.Gateway*',
                               'Microsoft.OpenClaw', 'OpenClawFoundation.OpenClaw', 'OpenClaw.Tray')) {
                foreach ($p in @(Get-AppxPackage -Name $name)) {
                    $packages += [ordered]@{
                        name = [string]$p.Name; version = [string]$p.Version
                        packageFullName = [string]$p.PackageFullName
                        packageFamilyName = [string]$p.PackageFamilyName
                        installLocation = [string]$p.InstallLocation
                        signatureKind = [string]$p.SignatureKind
                        isDevelopmentMode = [bool]$p.IsDevelopmentMode
                    }
                    if ($packages.Count -gt 32) { break }
                }
                if ($packages.Count -gt 32) { break }
            }
        } catch { $issues += 'current-user-package-inventory-failed' }
        try {
            foreach ($c in @(Get-Command clawctl -All -CommandType Application,ExternalScript -ErrorAction Stop)) {
                $commands += [ordered]@{
                    name = [string]$c.Name; commandType = [string]$c.CommandType; path = [string]$c.Path
                }
                if ($commands.Count -gt 32) { break }
            }
        } catch [System.Management.Automation.CommandNotFoundException] {
            $issues += 'clean-shell-command-not-found'
        } catch { $issues += 'clean-shell-command-resolution-failed' }
        [ordered]@{
            packages = @($packages | Select-Object -First 32)
            commands = @($commands | Select-Object -First 32)
            issues = @($issues)
            packagesTruncated = ($packages.Count -gt 32)
            commandsTruncated = ($commands.Count -gt 32)
        } | ConvertTo-Json -Depth 5 -Compress
        """;
}

internal sealed record DiagnosticStreamOutput(string Text, long CharactersRead)
{
    public bool Truncated => CharactersRead > Text.Length;
}

internal sealed record DiagnosticProcessOutput(
    int ExitCode,
    DiagnosticStreamOutput StandardOutput,
    DiagnosticStreamOutput StandardError);
