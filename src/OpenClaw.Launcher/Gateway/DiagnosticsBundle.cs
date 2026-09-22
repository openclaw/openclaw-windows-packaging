using System.IO.Compression;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Gateway;

/// <summary>What a diagnostics run produced.</summary>
internal sealed record DiagnosticsBundleResult(
    string? BundlePath,
    bool SessionReached,
    IReadOnlyList<string> Notes);

internal sealed partial class GatewayRuntime
{
    /// <summary>
    /// File names that must never reach a bundle, matched with a trailing
    /// wildcard.
    /// </summary>
    /// <remarks>
    /// These are the agent and shared credential stores. Provider tokens live
    /// in them and in their <c>-wal</c> sidecars, so excluding only the primary
    /// database would still ship the secret. The guest enforces this list too;
    /// keeping both checks means neither is a single point of failure.
    /// </remarks>
    internal static readonly string[] DeniedBundleNames =
    [
        "openclaw-agent.sqlite*",
        "openclaw.sqlite*",
        "auth-profiles*",
    ];

    /// <summary>Directory in the shared workspace used to stage guest files.</summary>
    internal const string StagingDirectoryName = "openclaw-diagnostics";

    /// <summary>
    /// Opens an interactive shell inside the recorded session.
    /// </summary>
    /// <remarks>
    /// Requires a recorded session but deliberately not completed setup: a
    /// half-configured installation is exactly what someone needs a shell to
    /// inspect. It never provisions, because a command whose job is diagnosis
    /// must not change what is being diagnosed.
    /// </remarks>
    public async Task<int> RunShellAsync(
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);

        SessionStatus status = Session.Coordinator.GetRecordedStatus();
        if (status.Record is null)
        {
            throw new SessionException(
                status.Detail ??
                "No agent session is recorded, so there is nothing to open a shell in. " +
                "Run `clawctl setup` first.");
        }

        SetupStateResult setup = Session.SetupState.Read(Session.ApplicationId);
        if (setup.Record?.Phase != SetupPhase.Ready)
        {
            await output.WriteLineAsync(
                "Warning: setup is not complete, so the session may lack its configuration. " +
                "Run `clawctl setup` to finish it.").ConfigureAwait(false);
        }

        SessionRecord record = await Session.Coordinator
            .StartRecordedAsync(cancellationToken).ConfigureAwait(false);
        string helperPath = Session.RequireStagedHelper(record);
        AgentShell shell = AgentShellResolver.Resolve(FileExists);

        await output.WriteLineAsync(
            $"Opening {shell.DisplayName} as {record.AgentUserName ?? "the agent account"} " +
            "in the isolated session.").ConfigureAwait(false);
        await output.WriteLineAsync("Exit the shell to return.").ConfigureAwait(false);

        return await Session.Executor.ExecuteCommandAsync(
            record,
            new SessionCommandRequest(
                helperPath,
                shell.ExecutablePath,
                AgentShellResolver.BuildInteractiveArguments(
                    record.WorkspacePath!,
                    record.AgentUserName ?? "agent"),
                record.WorkspacePath!),
            $"Opening {shell.DisplayName} in the isolated session.",
            shell.DisplayName,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bundles host-side, and where reachable agent-side, diagnostics.
    /// </summary>
    /// <remarks>
    /// Producing a file is the point of the command, so reaching the session is
    /// best-effort: someone runs this because something is broken, and a
    /// host-only bundle is still worth handing over.
    /// </remarks>
    public async Task<DiagnosticsBundleResult> CollectLogsAsync(
        string? requestedPath,
        CancellationToken cancellationToken)
    {
        string bundlePath = ResolveBundlePath(
            requestedPath,
            _paths,
            Environment.CurrentDirectory,
            _clock);

        List<string> notes = [];
        List<(string Name, string Path)> hostFiles = CollectHostFiles();
        string? staged = null;
        SessionWorkspaceOperation? stagingOperation = null;

        try
        {
            (staged, stagingOperation, bool sessionReached) =
                await TryStageAgentFilesAsync(notes, cancellationToken).ConfigureAwait(false);

            if (hostFiles.Count == 0 && staged is null)
            {
                return new DiagnosticsBundleResult(null, sessionReached, notes);
            }

            try
            {
                WriteBundle(bundlePath, hostFiles, staged, stagingOperation, notes);
            }
            catch (IOException exception) when (File.Exists(bundlePath))
            {
                throw new IOException(
                    $"The diagnostics bundle already exists: {bundlePath}. " +
                    "Choose another path with --output.",
                    exception);
            }
            return new DiagnosticsBundleResult(bundlePath, sessionReached, notes);
        }
        finally
        {
            TryDeleteStaging(stagingOperation, staged);
            stagingOperation?.Dispose();
        }
    }

    internal static string ResolveBundlePath(
        string? requestedPath,
        HostPaths paths,
        string currentDirectory,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        return requestedPath is null
            ? Path.Combine(
                paths.StateRoot,
                $"openclaw-diagnostics-{(clock ?? TimeProvider.System).GetUtcNow():yyyyMMdd-HHmmss}.zip")
            : Path.GetFullPath(requestedPath, currentDirectory);
    }

    private List<(string Name, string Path)> CollectHostFiles()
    {
        List<(string Name, string Path)> files = [];
        Add("host/openclaw.log", _paths.LogPath);
        Add("host/pre-reset.log", _paths.PreResetReportPath);
        Add("host/setup.json", _paths.SetupStatePath);
        Add("host/session.json", _paths.SessionStatePath);
        Add("host/gateway-config.json", _paths.GatewayConfigurationPath);
        Add("host/gateway-state.json", _paths.GatewayStatePath);
        Add("host/gateway-launcher.cmd", _paths.GatewayLauncherPath);
        return files;

        void Add(string name, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                files.Add((name, path));
            }
        }
    }

    private async Task<(
        string? Staged,
        SessionWorkspaceOperation? Operation,
        bool Reached)>
        TryStageAgentFilesAsync(
        List<string> notes,
        CancellationToken cancellationToken)
    {
        SessionStatus status = Session.Coordinator.GetRecordedStatus();
        if (status.Record?.WorkspacePath is not { Length: > 0 } workspace)
        {
            notes.Add(
                status.Detail ??
                "No agent session is recorded, so agent-side logs were not collected.");
            return (null, null, false);
        }

        string staged = Path.Combine(
            workspace, StagingDirectoryName, Guid.NewGuid().ToString("N"));
        SessionWorkspaceOperation? operation = null;

        try
        {
            string helperPath = Session.RequireStagedHelper(status.Record);
            SessionRecord record = await Session.Coordinator
                .StartRecordedAsync(cancellationToken).ConfigureAwait(false);
            operation = Session.Executor.CreateWorkspaceOperation(record);

            SessionCollectResult result = await Session.Executor
                .CollectAsync(
                    record,
                    helperPath,
                    staged,
                    BuildAgentSources(),
                    DeniedBundleNames,
                    cancellationToken)
                .ConfigureAwait(false);

            foreach (SessionCollectEntry entry in result.Entries ?? [])
            {
                if (!entry.Copied)
                {
                    notes.Add($"agent/{entry.Name}: {entry.Detail ?? "not collected"}");
                }
            }

            return (
                staged,
                operation,
                true);
        }
        catch (Exception exception) when (
            exception is SessionException or MxcException or SessionLaunchException or
            IOException or UnauthorizedAccessException)
        {
            notes.Add(
                $"Agent-side logs could not be collected ({exception.Message}). " +
                "The bundle contains host-side diagnostics only.");
            TryDeleteStaging(operation, staged);
            operation?.Dispose();
            return (null, null, false);
        }
    }

    /// <summary>
    /// The agent-side artifacts worth collecting, named relative to the agent
    /// profile so the bundle layout never carries the account name.
    /// </summary>
    private static IReadOnlyList<SessionCollectSource> BuildAgentSources()
    {
        return
        [
            new SessionCollectSource
            {
                RelativePath = @"AppData\Local\Temp\openclaw",
                Name = "logs",
                Pattern = "*.log",
                Recursive = true
            },
            new SessionCollectSource
            {
                RelativePath = ".openclaw",
                Name = "config",
                Pattern = "openclaw.json*"
            },
        ];
    }

    private static void WriteBundle(
        string bundlePath,
        List<(string Name, string Path)> hostFiles,
        string? staged,
        SessionWorkspaceOperation? stagingOperation,
        List<string> notes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(bundlePath)!);

        using FileStream stream = new(
            bundlePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);

        foreach ((string name, string path) in hostFiles)
        {
            AddEntry(archive, name, path, notes);
        }

        if (staged is not null &&
            stagingOperation is not null &&
            Directory.Exists(staged))
        {
            stagingOperation.EnsureCurrent();
            foreach (string file in EnumerateStagedFiles(
                stagingOperation.WorkspacePath,
                staged,
                notes))
            {
                string relative = Path.GetRelativePath(staged, file).Replace('\\', '/');
                AddEntry(
                    archive,
                    $"agent/{relative}",
                    file,
                    notes,
                    stagingOperation);
            }
        }

        ZipArchiveEntry manifest = archive.CreateEntry("manifest.txt");
        using StreamWriter writer = new(manifest.Open());
        writer.WriteLine($"Collected: {DateTimeOffset.UtcNow:u}");
        writer.WriteLine(
            "Redaction is best-effort and targets credential-shaped values. " +
            "Review before sharing.");
        foreach (string note in notes)
        {
            writer.WriteLine(note);
        }
    }

    private static List<string> EnumerateStagedFiles(
        string workspace,
        string staged,
        List<string> notes)
    {
        List<string> files = [];
        try
        {
            TrustedPath.EnsureNoReparsePoints(workspace, staged);
            Stack<string> pending = new();
            pending.Push(staged);
            while (pending.TryPop(out string? directory))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        notes.Add(
                            $"agent/{Path.GetRelativePath(staged, entry).Replace('\\', '/')}: " +
                            "excluded because it is a reparse point");
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                    }
                    else
                    {
                        TrustedPath.EnsureNoReparsePoints(workspace, entry);
                        files.Add(entry);
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is SessionException or IOException or UnauthorizedAccessException)
        {
            notes.Add(
                $"Agent-side staging could not be read safely ({exception.Message}).");
        }

        return files;
    }

    private static void AddEntry(
        ZipArchive archive,
        string name,
        string path,
        List<string> notes,
        SessionWorkspaceOperation? operation = null)
    {
        string fileName = Path.GetFileName(path);

        // The guest already refused these. Re-checking here covers host-side
        // sources and any future caller that stages files another way.
        if (SessionCollectProtocol.IsDenied(fileName, DeniedBundleNames))
        {
            notes.Add($"{name}: excluded by policy");
            return;
        }

        try
        {
            string text;
            using FileStream input = operation is null
                ? new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete)
                : operation.OpenRead(path);
            using (StreamReader reader = new(input))
            {
                text = reader.ReadToEnd();
            }

            ZipArchiveEntry entry = archive.CreateEntry(name);
            using StreamWriter writer = new(entry.Open());
            writer.Write(DiagnosticsRedactor.Redact(text));
        }
        catch (Exception exception) when (
            exception is SessionException or IOException or UnauthorizedAccessException)
        {
            notes.Add($"{name}: unreadable ({exception.Message})");
        }
    }

    private static void TryDeleteStaging(
        SessionWorkspaceOperation? operation,
        string? staged)
    {
        if (operation is null || staged is null)
        {
            return;
        }

        try
        {
            operation.Delete(staged);
        }
        catch (Exception exception) when (
            exception is SessionException or IOException or UnauthorizedAccessException)
        {
        }
    }

}
