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
        byte[] existing = File.Exists(profilePath) ? File.ReadAllBytes(profilePath) : [];
        byte[] block = Encoding.UTF8.GetBytes(
            $"{BeginMarker}{NewLine(existing)}{Script.TrimEnd()}{NewLine(existing)}{EndMarker}");
        WriteAtomically(profilePath, ReplaceBlock(existing, block));
    }

    internal static void Uninstall(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        if (!File.Exists(profilePath))
        {
            return;
        }

        byte[] existing = File.ReadAllBytes(profilePath);
        int begin = Find(existing, BeginMarker);
        if (begin < 0)
        {
            return;
        }
        int end = Find(existing, EndMarker, begin);
        int removeStart = begin;
        if (begin >= 4 && existing.AsSpan(begin - 4, 4).SequenceEqual("\r\n\r\n"u8))
        {
            removeStart -= 4;
        }
        else if (begin >= 2 && existing.AsSpan(begin - 2, 2).SequenceEqual("\n\n"u8))
        {
            removeStart -= 2;
        }
        int removeEnd = end < 0
            ? existing.Length
            : end + EndMarker.Length;
        if (existing.AsSpan(removeEnd).StartsWith("\r\n"u8))
        {
            removeEnd += 2;
        }
        else if (existing.AsSpan(removeEnd).StartsWith("\n"u8))
        {
            removeEnd++;
        }
        byte[] updated = end < 0
            ? existing.AsSpan(0, removeStart).ToArray()
            : [.. existing.AsSpan(0, removeStart), .. existing.AsSpan(removeEnd)];
        WriteAtomically(profilePath, updated);
    }

    internal static void WriteScriptAtomically(string path, string script) =>
        WriteAtomically(path, Encoding.UTF8.GetBytes(script));

    private static byte[] ReplaceBlock(byte[] existing, byte[] block)
    {
        int begin = Find(existing, BeginMarker);
        if (begin < 0)
        {
            return existing.Length == 0
                ? [.. block, .. Encoding.UTF8.GetBytes(NewLine(existing))]
                : [.. existing, .. Encoding.UTF8.GetBytes(NewLine(existing) + NewLine(existing)), .. block, .. Encoding.UTF8.GetBytes(NewLine(existing))];
        }

        int end = Find(existing, EndMarker, begin);
        return end < 0
            ? [.. existing.AsSpan(0, begin), .. block, .. Encoding.UTF8.GetBytes(NewLine(existing))]
            : [.. existing.AsSpan(0, begin), .. block, .. existing.AsSpan(end + EndMarker.Length)];
    }

    private static string NewLine(byte[] bytes) =>
        bytes.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";

    private static int Find(byte[] bytes, string text, int start = 0) =>
        bytes.AsSpan(start).IndexOf(Encoding.UTF8.GetBytes(text)) is int index && index >= 0
            ? start + index
            : -1;

    private static void WriteAtomically(string path, byte[] content)
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
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
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
