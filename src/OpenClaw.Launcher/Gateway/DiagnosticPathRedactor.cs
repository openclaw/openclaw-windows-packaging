namespace OpenClaw.Launcher.Gateway;

/// <summary>Retains drive and directory depth without exporting private path components.</summary>
internal sealed class DiagnosticPathRedactor
{
    private readonly Dictionary<string, string> _privateParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _publicParts = new(PublicParts, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> PublicParts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users", "AppData", "Local", "LocalLow", "Roaming", "Microsoft", "WindowsApps",
        "Packages", "LocalState", "Windows", "Program Files", "Program Files (x86)",
        "System32", "SysWOW64", "Temp", "openclaw.exe", "clawctl.exe", "clawctl.cmd",
        "clawctl.bat", "clawctl.ps1", "layout", "artifacts", "local-package", "patches", "x64", "arm64"
    };

    public DiagnosticPathRedactor(IEnumerable<string>? publicParts = null)
    {
        if (publicParts is not null)
        {
            _publicParts.UnionWith(publicParts);
        }
    }

    public string? Redact(string? path)
    {
        if (path is null)
        {
            return null;
        }
        string normalized = path.Replace('/', '\\');
        string prefix = normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ? @"\\?\" : string.Empty;
        normalized = normalized[prefix.Length..];
        bool extendedNetwork = prefix.Length > 0 &&
            normalized.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase);
        if (extendedNetwork)
        {
            prefix += normalized[..4];
            normalized = normalized[4..];
        }
        bool network = normalized.StartsWith(@"\\", StringComparison.Ordinal);
        string[] parts = normalized.Split('\\');
        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0 || (index == 0 && part.Length == 2 && char.IsAsciiLetter(part[0]) && part[1] == ':'))
            {
                continue;
            }
            bool user = index > 0 && parts[index - 1].Equals("Users", StringComparison.OrdinalIgnoreCase);
            bool networkIdentity = (network && index is 2 or 3) ||
                (extendedNetwork && index is 0 or 1);
            if (!user && !networkIdentity && _publicParts.Contains(part))
            {
                continue;
            }
            if (!_privateParts.TryGetValue(part, out string? placeholder))
            {
                placeholder = $"<private-{_privateParts.Count + 1}>";
                _privateParts.Add(part, placeholder);
            }
            parts[index] = placeholder;
        }
        return prefix + string.Join('\\', parts);
    }
}
