using System.Text;

namespace OpenClaw.Launcher;

internal static class PowerShellCompletion
{
    internal const string BeginMarker = "# >>> openclaw completion >>>";
    internal const string EndMarker = "# <<< openclaw completion <<<";
    internal const string OpenClawScriptRelativePath = @"shell-completions\openclaw.ps1";
    private static readonly Encoding Utf32LittleEndian =
        new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true);
    private static readonly Encoding Utf32BigEndian =
        new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true);

    internal static string ClawCtlScript { get; } = """
        # PowerShell completion for clawctl.
        Register-ArgumentCompleter -Native -CommandName clawctl -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)

            $cursorInCommand = $cursorPosition - $commandAst.Extent.StartOffset
            $directive = "[suggest:$cursorInCommand]"
            & clawctl $directive $commandAst.Extent.Text 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }
        """;

    internal static string ProfileLoaderScript { get; } = """
        & {
            $clawctlCommand = Get-Command clawctl -CommandType Application -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -eq $clawctlCommand) {
                return
            }

            [string[]] $completionLines = @(& $clawctlCommand.Source completion)
            if ($LASTEXITCODE -ne 0 -or $completionLines.Count -eq 0) {
                return
            }

            Invoke-Expression ([string]::Join([Environment]::NewLine, $completionLines))
        }
        """;

    internal static string DefaultProfilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "PowerShell",
        "Microsoft.PowerShell_profile.ps1");

    internal static string BuildScript(
        string openClawScript,
        string newLine = "\n")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openClawScript);
        return NormalizeNewLines(ClawCtlScript.TrimEnd(), newLine) +
            newLine + newLine +
            NormalizeNewLines(openClawScript.TrimEnd(), newLine) +
            newLine;
    }

    internal static string ReadPackagedOpenClawScript(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        string path = Path.Combine(applicationDirectory, OpenClawScriptRelativePath);
        try
        {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            throw new InvalidDataException(
                $"The packaged OpenClaw completion script could not be read: {path}",
                exception);
        }
    }

    internal static string Install(
        string profilePath,
        string? baseDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        profilePath = Path.GetFullPath(profilePath, baseDirectory ?? Environment.CurrentDirectory);
        ProfileText profile = ReadProfile(profilePath);
        ValidateMarkers(profile.Text, out int begin, out int end);
        string block = BeginMarker + profile.NewLine +
            NormalizeNewLines(ProfileLoaderScript.TrimEnd(), profile.NewLine) +
            profile.NewLine + EndMarker;
        string updated = begin < 0
            ? string.IsNullOrEmpty(profile.Text)
                ? block + profile.NewLine
                : profile.Text + profile.NewLine + block + profile.NewLine
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
        if (removeStart > 0 &&
            profile.Text.AsSpan(0, removeStart).EndsWith(profile.NewLine, StringComparison.Ordinal))
        {
            removeStart -= profile.NewLine.Length;
        }
        int removeEnd = end + EndMarker.Length;
        if (profile.Text.AsSpan(removeEnd).StartsWith(profile.NewLine, StringComparison.Ordinal))
        {
            removeEnd += profile.NewLine.Length;
        }
        string before = profile.Text[..removeStart];
        string after = profile.Text[removeEnd..];
        string separator =
            before.Length > 0 &&
            after.Length > 0 &&
            !before.EndsWith(profile.NewLine, StringComparison.Ordinal) &&
            !after.StartsWith(profile.NewLine, StringComparison.Ordinal)
                ? profile.NewLine
                : string.Empty;
        WriteAtomically(profilePath, profile.Encode(before + separator + after));
        return profilePath;
    }

    internal static void WriteScriptAtomically(string path, string script) =>
        WriteAtomically(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script));

    internal static bool SynchronizeCacheIfInstalled(string path, string script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(script);
        if (!File.Exists(path))
        {
            return false;
        }

        byte[] expected =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(script);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected))
        {
            WriteAtomically(path, expected);
        }
        return true;
    }

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
        else if (bytes.AsSpan().StartsWith(Utf32LittleEndian.Preamble))
        {
            encoding = Utf32LittleEndian;
            preambleLength = Utf32LittleEndian.Preamble.Length;
        }
        else if (bytes.AsSpan().StartsWith(Utf32BigEndian.Preamble))
        {
            encoding = Utf32BigEndian;
            preambleLength = Utf32BigEndian.Preamble.Length;
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

    private static string NormalizeNewLines(string text, string newLine) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newLine, StringComparison.Ordinal);

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
