using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--collect</c> mode: stages named diagnostic files into the shared
/// workspace so the host can read them.
/// </summary>
/// <remarks>
/// <para>
/// The agent profile is ACL'd against the invoking user. Without this the host
/// cannot read the agent's own OpenClaw logs at all, which is exactly the state
/// someone is in when they need to report a problem.
/// </para>
/// <para>
/// Every source is named by the host. A missing source is reported, never
/// treated as a failure, because collection is most useful precisely when an
/// installation is incomplete.
/// </para>
/// </remarks>
internal static class SessionCollector
{
    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        string? profileRoot = null)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;
        string profile = profileRoot ?? Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);

        try
        {
            SessionCollectRequest request =
                SessionCollectProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;

            Directory.CreateDirectory(request.DestinationDirectory!);
            List<SessionCollectEntry> entries = [];

            foreach (SessionCollectSource source in request.Sources ?? [])
            {
                Collect(request, source, profile, entries);
            }

            writeFile(
                resultPath,
                SessionCollectProtocol.SerializeResult(new SessionCollectResult
                {
                    RequestId = requestId,
                    Entries = entries
                }));
            return 0;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            TryWriteFailure(writeFile, resultPath, requestId, exception.Message);
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    private static void Collect(
        SessionCollectRequest request,
        SessionCollectSource source,
        string profile,
        List<SessionCollectEntry> entries)
    {
        string path;
        try
        {
            path = ResolveInProfile(profile, source.RelativePath!);
        }
        catch (SessionLaunchException exception)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = exception.Message
            });
            return;
        }

        if (Directory.Exists(path))
        {
            CollectDirectory(request, source, path, entries);
            return;
        }

        if (!File.Exists(path))
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = "not present"
            });
            return;
        }

        CopyOne(request, path, source.Name!, entries);
    }

    private static void CollectDirectory(
        SessionCollectRequest request,
        SessionCollectSource source,
        string root,
        List<SessionCollectEntry> entries)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(
                root,
                string.IsNullOrWhiteSpace(source.Pattern) ? "*" : source.Pattern,
                source.Recursive
                    ? SearchOption.AllDirectories
                    : SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = $"unreadable: {exception.Message}"
            });
            return;
        }

        bool any = false;
        foreach (string file in files)
        {
            any = true;
            string relative = Path.GetRelativePath(root, file);
            CopyOne(
                request,
                file,
                Path.Combine(source.Name!, relative).Replace('\\', '/'),
                entries);
        }

        if (!any)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = source.Name,
                Copied = false,
                Detail = "empty"
            });
        }
    }

    /// <summary>
    /// Resolves a source against this account's profile.
    /// </summary>
    /// <remarks>
    /// Only the guest can do this: the profile directory is named after a
    /// generated account and is ACL'd against the host user. The resolved path
    /// is re-checked against the profile root because <c>Path.Combine</c>
    /// on a rooted segment silently discards the root it was given.
    /// </remarks>
    internal static string ResolveInProfile(string profile, string relativePath)
    {
        string resolved = Path.GetFullPath(Path.Combine(profile, relativePath));
        string root = Path.GetFullPath(profile).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? resolved
            : throw new SessionLaunchException(
                "resolves outside the agent profile");
    }

    private static void CopyOne(
        SessionCollectRequest request,
        string sourcePath,
        string name,
        List<SessionCollectEntry> entries)
    {
        string fileName = Path.GetFileName(sourcePath);
        if (SessionCollectProtocol.IsDenied(fileName, request.DeniedNames))
        {
            entries.Add(new SessionCollectEntry
            {
                Name = name,
                Copied = false,
                Detail = "excluded by policy"
            });
            return;
        }

        string destination = Path.Combine(
            request.DestinationDirectory!,
            name.Replace('/', Path.DirectorySeparatorChar));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            // Shared read/write/delete because the agent's own OpenClaw
            // processes hold their logs open while they run; an exclusive open
            // would fail on exactly the live installation worth collecting.
            using (FileStream input = new(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (FileStream output = new(
                destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            entries.Add(new SessionCollectEntry
            {
                Name = name,
                Copied = true,
                Length = new FileInfo(destination).Length
            });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            entries.Add(new SessionCollectEntry
            {
                Name = name,
                Copied = false,
                Detail = exception.Message
            });
        }
    }

    private static void TryWriteFailure(
        Action<string, string> writeFile,
        string resultPath,
        string? requestId,
        string error)
    {
        try
        {
            writeFile(
                resultPath,
                SessionCollectProtocol.SerializeResult(new SessionCollectResult
                {
                    RequestId = requestId,
                    Error = error
                }));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The exit code is the only remaining channel.
        }
    }
}
