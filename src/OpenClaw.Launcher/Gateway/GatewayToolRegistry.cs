using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Persistent, package-owned registrations for commands intentionally exposed
/// to the isolated Gateway. This is deliberately provider-neutral: it knows
/// about executable aliases and Gateway runtime locations, not OAuth or a
/// particular skill's configuration format.
/// </summary>
internal sealed class GatewayToolRegistry
{
    private const int SchemaVersion = 1;
    private static readonly Regex CommandPattern = new(
        "^[a-z][a-z0-9-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _storePath;

    public GatewayToolRegistry(string storePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        _storePath = Path.GetFullPath(storePath);
    }

    /// <summary>
    /// Lists registrations without returning the selected executable paths to
    /// callers that only need status.
    /// </summary>
    public IReadOnlyList<GatewayToolStatus> List()
    {
        GatewayToolStore store = Read();
        return [.. store.Tools
            .OrderBy(tool => tool.Command, StringComparer.OrdinalIgnoreCase)
            .Select(ToStatus)];
    }

    /// <summary>
    /// Creates or replaces a command registration. The caller must have
    /// already received explicit interactive approval for the executable.
    /// </summary>
    public GatewayToolStatus Register(
        string command,
        string executablePath,
        GatewayToolSource source,
        string? displayName = null)
    {
        string normalizedCommand = NormalizeCommand(command);
        string normalizedPath = NormalizeExecutablePath(executablePath);
        GatewayToolStore store = Read();
        GatewayToolRegistration? existing = store.Tools.FirstOrDefault(tool =>
            string.Equals(tool.Command, normalizedCommand, StringComparison.OrdinalIgnoreCase));

        var registration = new GatewayToolRegistration
        {
            RegistrationId = existing?.RegistrationId ?? $"toolreg_{Guid.NewGuid():N}",
            Command = normalizedCommand,
            DisplayName = NormalizeDisplayName(displayName),
            ExecutablePath = normalizedPath,
            Source = source,
            Enabled = existing?.Enabled ?? true,
            RuntimeProfileId = existing?.RuntimeProfileId ?? $"toolprofile_{Guid.NewGuid():N}",
        };

        store.Tools.RemoveAll(tool => string.Equals(
            tool.Command, normalizedCommand, StringComparison.OrdinalIgnoreCase));
        store.Tools.Add(registration);
        Write(store);
        return ToStatus(registration);
    }

    public GatewayToolStatus SetEnabled(string command, bool enabled)
    {
        string normalizedCommand = NormalizeCommand(command);
        GatewayToolStore store = Read();
        GatewayToolRegistration registration = store.Tools.FirstOrDefault(tool =>
            string.Equals(tool.Command, normalizedCommand, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No Gateway tool is registered as '{normalizedCommand}'.");
        registration.Enabled = enabled;
        Write(store);
        return ToStatus(registration);
    }

    public bool Unregister(string command)
    {
        string normalizedCommand = NormalizeCommand(command);
        GatewayToolStore store = Read();
        int removed = store.Tools.RemoveAll(tool => string.Equals(
            tool.Command, normalizedCommand, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Write(store);
        }

        return removed > 0;
    }

    /// <summary>
    /// Materializes only enabled, existing registrations as command shims in a
    /// session-shared directory. The returned directory is intended solely as
    /// a per-process Gateway PATH prefix; it must not be appended to User or
    /// Machine PATH.
    /// </summary>
    public GatewayToolRuntime PrepareRuntime(string sharedWorkspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedWorkspacePath);
        string root = Path.GetFullPath(sharedWorkspacePath);
        string shimDirectory = Path.Combine(root, ".openclaw", "gateway-tools", "shims");
        string profileRoot = Path.Combine(root, ".openclaw", "gateway-tools", "profiles");
        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(profileRoot);

        GatewayToolStore store = Read();
        var statuses = new List<GatewayToolStatus>();
        var expectedShims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GatewayToolRegistration tool in store.Tools)
        {
            GatewayToolStatus status = ToStatus(tool);
            statuses.Add(status);
            if (!tool.Enabled || status.Availability != GatewayToolAvailability.Ready)
            {
                continue;
            }

            string profile = Path.Combine(profileRoot, tool.RuntimeProfileId);
            Directory.CreateDirectory(profile);
            string shimPath = Path.Combine(shimDirectory, tool.Command + ".cmd");
            WriteShim(shimPath, tool.ExecutablePath);
            expectedShims.Add(Path.GetFullPath(shimPath));
        }

        foreach (string candidate in Directory.EnumerateFiles(shimDirectory, "*.cmd"))
        {
            if (!expectedShims.Contains(Path.GetFullPath(candidate)))
            {
                File.Delete(candidate);
            }
        }

        return new GatewayToolRuntime(shimDirectory, profileRoot, statuses);
    }

    internal static string NormalizeCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        string normalized = command.Trim();
        if (!CommandPattern.IsMatch(normalized) ||
            normalized.Equals("openclaw", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("clawctl", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Gateway tool commands must be a lowercase executable alias and cannot replace OpenClaw or shell commands.",
                nameof(command));
        }

        return normalized;
    }

    private static string NormalizeExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        string normalized = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetExtension(normalized), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(normalized))
        {
            throw new ArgumentException(
                "The selected Gateway tool must be an existing .exe file.",
                nameof(executablePath));
        }

        return normalized;
    }

    private static string? NormalizeDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        return displayName.Trim() is { Length: <= 120 } normalized
            ? normalized
            : throw new ArgumentException("The display name is too long.", nameof(displayName));
    }

    private static GatewayToolStatus ToStatus(GatewayToolRegistration registration) =>
        new(
            registration.RegistrationId,
            registration.Command,
            registration.DisplayName,
            registration.Source,
            registration.Enabled,
            File.Exists(registration.ExecutablePath)
                ? GatewayToolAvailability.Ready
                : GatewayToolAvailability.ExecutableUnavailable,
            registration.RuntimeProfileId);

    private static void WriteShim(string shimPath, string executablePath)
    {
        // The executable path was canonicalized when registered. A .cmd shim
        // carries no user input other than %*, which cmd forwards as arguments.
        string content = $"@echo off{Environment.NewLine}\"{executablePath}\" %*{Environment.NewLine}";
        File.WriteAllText(shimPath, content);
    }

    private GatewayToolStore Read()
    {
        if (!File.Exists(_storePath))
        {
            return new GatewayToolStore { SchemaVersion = SchemaVersion };
        }

        GatewayToolStore? store = JsonSerializer.Deserialize<GatewayToolStore>(
            File.ReadAllText(_storePath), JsonOptions);
        if (store is null || store.SchemaVersion != SchemaVersion)
        {
            throw new InvalidOperationException("The Gateway tool registration store is not supported.");
        }

        store.Tools ??= [];
        return store;
    }

    private void Write(GatewayToolStore store)
    {
        string? directory = Path.GetDirectoryName(_storePath);
        Directory.CreateDirectory(directory ?? throw new InvalidOperationException(
            "The Gateway tool registration store has no parent directory."));
        string temporary = _storePath + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOptions));
        File.Move(temporary, _storePath, overwrite: true);
    }

    private sealed class GatewayToolStore
    {
        public int SchemaVersion { get; set; }
        public List<GatewayToolRegistration> Tools { get; set; } = [];
    }

    private sealed class GatewayToolRegistration
    {
        public string RegistrationId { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public GatewayToolSource Source { get; set; }
        public bool Enabled { get; set; }
        public string RuntimeProfileId { get; set; } = string.Empty;
    }
}

internal enum GatewayToolSource
{
    Desktop,
    GatewayTools,
}

internal enum GatewayToolAvailability
{
    Ready,
    ExecutableUnavailable,
}

internal sealed record GatewayToolStatus(
    string RegistrationId,
    string Command,
    string? DisplayName,
    GatewayToolSource Source,
    bool Enabled,
    GatewayToolAvailability Availability,
    string RuntimeProfileId);

internal sealed record GatewayToolRuntime(
    string ShimDirectory,
    string ProfileRoot,
    IReadOnlyList<GatewayToolStatus> Tools);
