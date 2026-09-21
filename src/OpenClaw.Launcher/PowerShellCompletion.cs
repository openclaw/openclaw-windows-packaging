using System.Text;

namespace OpenClaw.Launcher;

internal static class PowerShellCompletion
{
    internal const string BeginMarker = "# >>> openclaw completion >>>";
    internal const string EndMarker = "# <<< openclaw completion <<<";

    internal static string Script { get; } = """
        # PowerShell completion for clawctl.
        Register-ArgumentCompleter -Native -CommandName clawctl -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $words = @($commandAst.CommandElements | Select-Object -Skip 1 | ForEach-Object { $_.Extent.Text })
            $directive = "[suggest:$($words.Count + 1)]"
            & clawctl $directive @words 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    internal static string DefaultProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PowerShell",
        "Microsoft.PowerShell_profile.ps1");

    internal static void Install(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        string existing = File.Exists(profilePath) ? File.ReadAllText(profilePath) : string.Empty;
        string updated = ReplaceBlock(existing, $"{BeginMarker}{Environment.NewLine}{Script.TrimEnd()}{Environment.NewLine}{EndMarker}");
        WriteAtomically(profilePath, updated);
    }

    internal static void Uninstall(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        if (!File.Exists(profilePath))
        {
            return;
        }

        string existing = File.ReadAllText(profilePath);
        int begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return;
        }
        int end = existing.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        string updated = end < 0
            ? existing[..begin]
            : existing.Remove(begin, end + EndMarker.Length - begin)
                .TrimEnd('\r', '\n') + Environment.NewLine;
        WriteAtomically(profilePath, updated);
    }

    private static string ReplaceBlock(string existing, string block)
    {
        int begin = existing.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return string.IsNullOrEmpty(existing)
                ? $"{block}{Environment.NewLine}"
                : $"{existing.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{block}{Environment.NewLine}";
        }

        int end = existing.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        return end < 0
            ? existing[..begin] + block + Environment.NewLine
            : existing[..begin] + block + existing[(end + EndMarker.Length)..];
    }

    private static void WriteAtomically(string path, string content)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The profile path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
