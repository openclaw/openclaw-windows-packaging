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

            $directive = "[suggest:$cursorPosition]"
            & clawctl $directive $commandAst.Extent.Text 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    internal static string DefaultProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PowerShell",
        "Microsoft.PowerShell_profile.ps1");

    internal static string Install(string profilePath, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        profilePath = Path.GetFullPath(profilePath, baseDirectory ?? Environment.CurrentDirectory);
        ProfileText profile = ReadProfile(profilePath);
        ValidateMarkers(profile.Text, out int begin, out int end);
        string block = $"{BeginMarker}{profile.NewLine}{Script.TrimEnd()}{profile.NewLine}{EndMarker}";
        string updated = begin < 0
            ? string.IsNullOrEmpty(profile.Text) ? block + profile.NewLine : profile.Text.TrimEnd() + profile.NewLine + profile.NewLine + block + profile.NewLine
            : profile.Text[..begin] + block + profile.Text[(end + EndMarker.Length)..];
        WriteAtomically(profilePath, profile.Encode(updated));
        return profilePath;
    }

    internal static string Uninstall(string profilePath, string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        profilePath = Path.GetFullPath(profilePath, baseDirectory ?? Environment.CurrentDirectory);
        if (!File.Exists(profilePath))
        {
            return profilePath;
        }

        ProfileText profile = ReadProfile(profilePath);
        ValidateMarkers(profile.Text, out int begin, out int end);
        if (begin < 0)
        {
            return profilePath;
        }

        int removeStart = begin;
        if (removeStart >= profile.NewLine.Length &&
            profile.Text.AsSpan(0, removeStart).EndsWith(profile.NewLine + profile.NewLine, StringComparison.Ordinal))
        {
            removeStart -= profile.NewLine.Length;
        }
        int removeEnd = end + EndMarker.Length;
        if (profile.Text.AsSpan(removeEnd).StartsWith(profile.NewLine, StringComparison.Ordinal))
        {
            removeEnd += profile.NewLine.Length;
        }
        WriteAtomically(profilePath, profile.Encode(profile.Text.Remove(removeStart, removeEnd - removeStart)));
        return profilePath;
    }

    internal static void WriteScriptAtomically(string path, string script) =>
        WriteAtomically(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script));

    private static ProfileText ReadProfile(string path)
    {
        byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        int preambleLength = 0;
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
            preambleLength = Encoding.UTF8.Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            encoding = Encoding.Unicode;
            preambleLength = Encoding.Unicode.Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            encoding = Encoding.BigEndianUnicode;
            preambleLength = Encoding.BigEndianUnicode.Preamble.Length;
        }

        try
        {
            string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            return new ProfileText(text, encoding, preambleLength != 0,
                text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"The PowerShell profile '{path}' is not valid {encoding.WebName} text.", exception);
        }
    }

    private static void ValidateMarkers(string text, out int begin, out int end)
    {
        begin = text.IndexOf(BeginMarker, StringComparison.Ordinal);
        end = text.IndexOf(EndMarker, StringComparison.Ordinal);
        bool valid = begin < 0 && end < 0 ||
            begin >= 0 && end > begin &&
            text.IndexOf(BeginMarker, begin + BeginMarker.Length, StringComparison.Ordinal) < 0 &&
            text.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) < 0;
        if (!valid)
        {
            throw new InvalidDataException("The PowerShell profile contains malformed OpenClaw completion markers. Remove or repair the marked block before retrying.");
        }
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new IOException($"The profile path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, contents);
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

    private sealed record ProfileText(string Text, Encoding Encoding, bool HasPreamble, string NewLine)
    {
        public byte[] Encode(string text) => HasPreamble
            ? [.. Encoding.GetPreamble(), .. Encoding.GetBytes(text)]
            : Encoding.GetBytes(text);
    }
}
