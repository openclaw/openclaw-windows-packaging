using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed record OpenClawSetupResult(int ExitCode, string StandardOutput, string StandardError);

internal static class WorkspaceEnvironmentInstructions
{
    internal const string StartMarker = "<!-- openclaw-windows-packaging:environment:start -->";
    internal const string EndMarker = "<!-- openclaw-windows-packaging:environment:end -->";

    private const string ManagedContent =
        """
        ## Windows isolation-session environment

        This OpenClaw instance runs under a dedicated package-managed account in an MXC isolation session on behalf of the signed-in Windows user.

        - This session has no interactive Windows desktop. Do not claim to open GUI windows, use the human user's clipboard, display notifications, or complete browser handoffs in the human user's session.
        - This account's profile, environment, installed-user state, and visible processes are not the signed-in user's state.
        - Prefer CLI, text, and available OpenClaw channels. When an action requires the human's interactive desktop, explain what the human needs to do instead of claiming it was performed.
        """;

    public static (string WorkspacePath, bool Updated) Apply(
        string nodePath,
        string applicationDirectory,
        string nativeRedirectPreloadPath,
        string? nativeRootPath,
        IReadOnlyDictionary<string, string> environment,
        Func<ProcessStartInfo, OpenClawSetupResult>? runSetup = null,
        Func<string>? getProfilePath = null)
    {
        string profilePath = (getProfilePath ?? AgentProfile.GetPath)();
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = profilePath
        };
        foreach ((string name, string value) in environment)
        {
            startInfo.Environment[name] = value;
        }
        SessionProcessLauncher.PrependPath(startInfo, Path.GetDirectoryName(nodePath));
        if (!string.IsNullOrWhiteSpace(nativeRootPath))
        {
            startInfo.ArgumentList.Add("--import");
            startInfo.ArgumentList.Add(new Uri(nativeRedirectPreloadPath).AbsoluteUri);
            startInfo.Environment["OPENCLAW_NATIVE_APP_ROOT"] = applicationDirectory;
            startInfo.Environment["OPENCLAW_NATIVE_STAGED_ROOT"] = nativeRootPath;
        }
        startInfo.ArgumentList.Add(Path.Combine(applicationDirectory, "openclaw.mjs"));
        startInfo.ArgumentList.Add("setup");
        startInfo.ArgumentList.Add("--json");

        OpenClawSetupResult setup = (runSetup ?? RunSetup)(startInfo);
        if (setup.ExitCode != 0)
        {
            string detail = string.IsNullOrWhiteSpace(setup.StandardError)
                ? $"exit code {setup.ExitCode}"
                : setup.StandardError.Trim();
            throw new SessionLaunchException(
                $"OpenClaw could not initialize its workspace: {detail}");
        }

        string workspacePath = ReadWorkspacePath(setup.StandardOutput);
        string resolvedProfile = Path.GetFullPath(profilePath);
        string resolvedWorkspace = Path.GetFullPath(workspacePath);
        string profilePrefix = resolvedProfile.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedProfile
            : resolvedProfile + Path.DirectorySeparatorChar;
        if (!resolvedWorkspace.StartsWith(profilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException(
                "OpenClaw setup returned a workspace outside the isolated agent profile.");
        }
        EnsureNoReparsePoints(resolvedProfile, resolvedWorkspace);
        return (resolvedWorkspace, UpdateAgentsFile(resolvedWorkspace));
    }

    internal static bool UpdateAgentsFile(string workspacePath)
    {
        if (!Path.IsPathFullyQualified(workspacePath))
        {
            throw new SessionLaunchException(
                "OpenClaw setup returned a workspace path that is not fully qualified.");
        }

        Directory.CreateDirectory(workspacePath);
        string agentsPath = Path.Combine(workspacePath, "AGENTS.md");
        if (File.Exists(agentsPath) &&
            File.GetAttributes(agentsPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new SessionLaunchException(
                "The OpenClaw workspace AGENTS.md is a reparse point.");
        }
        byte[] existingBytes = File.Exists(agentsPath) ? File.ReadAllBytes(agentsPath) : [];
        bool hasUtf8Preamble = existingBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        string existing;
        try
        {
            existing = new UTF8Encoding(false, true).GetString(
                existingBytes,
                hasUtf8Preamble ? Encoding.UTF8.Preamble.Length : 0,
                existingBytes.Length - (hasUtf8Preamble ? Encoding.UTF8.Preamble.Length : 0));
        }
        catch (DecoderFallbackException exception)
        {
            throw new SessionLaunchException(
                $"The OpenClaw workspace AGENTS.md is not valid UTF-8: {exception.Message}");
        }
        string newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string section = string.Join(
            newline,
            StartMarker,
            ManagedContent.ReplaceLineEndings(newline),
            EndMarker);
        string updated = ReplaceManagedSection(existing, section, newline);
        if (string.Equals(existing, updated, StringComparison.Ordinal))
        {
            return false;
        }

        string temporaryPath = Path.Combine(
            workspacePath,
            $".AGENTS.md.openclaw-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, updated, new UTF8Encoding(hasUtf8Preamble));
            File.Move(temporaryPath, agentsPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
        return true;
    }

    private static string ReplaceManagedSection(string existing, string section, string newline)
    {
        int start = existing.IndexOf(StartMarker, StringComparison.Ordinal);
        int end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
        if ((start < 0) != (end < 0) ||
            (start >= 0 && (end < start ||
                existing.IndexOf(StartMarker, start + StartMarker.Length, StringComparison.Ordinal) >= 0 ||
                existing.IndexOf(EndMarker, end + EndMarker.Length, StringComparison.Ordinal) >= 0)))
        {
            throw new SessionLaunchException(
                "AGENTS.md contains malformed package-managed environment markers.");
        }

        if (start >= 0)
        {
            int afterEnd = end + EndMarker.Length;
            return string.Concat(existing.AsSpan(0, start), section, existing.AsSpan(afterEnd));
        }

        if (existing.Length == 0)
        {
            return section + newline;
        }

        string separator = existing.EndsWith(newline + newline, StringComparison.Ordinal)
            ? string.Empty
            : existing.EndsWith(newline, StringComparison.Ordinal) ? newline : newline + newline;
        return existing + separator + section + newline;
    }

    private static string ReadWorkspacePath(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("workspaceDir", out JsonElement workspace) ||
                workspace.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(workspace.GetString()))
            {
                throw new SessionLaunchException(
                    "OpenClaw setup did not report its workspace directory.");
            }
            return workspace.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new SessionLaunchException(
                $"OpenClaw setup returned invalid JSON: {exception.Message}");
        }
    }

    private static void EnsureNoReparsePoints(string profilePath, string workspacePath)
    {
        string relativePath = Path.GetRelativePath(profilePath, workspacePath);
        string current = profilePath;
        foreach (string segment in relativePath.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new SessionLaunchException(
                    "OpenClaw setup returned a workspace through a reparse point.");
            }
        }
    }

    private static OpenClawSetupResult RunSetup(ProcessStartInfo startInfo)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new SessionLaunchException("OpenClaw setup could not be started.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new OpenClawSetupResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }
}
