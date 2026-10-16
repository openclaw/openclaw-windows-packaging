using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

internal sealed class SessionStateTransfer
{
    private readonly string _profile;
    private readonly string _directory;
    private readonly IStateArchiveApplication _application;
    private readonly SessionStateAccess _access;
    private readonly Action<string, string> _move;

    public SessionStateTransfer(
        string profile,
        string directory,
        IStateArchiveApplication application,
        Action<string, string>? move = null)
    {
        _profile = Path.GetFullPath(profile);
        _directory = Path.GetFullPath(directory);
        _application = application;
        _access = new SessionStateAccess(_directory);
        _move = move ?? MoveEntry;
    }

    public SessionStateTransferResult Execute(SessionStateTransferRequest request)
    {
        return request.Action switch
        {
            SessionStateTransferAction.Inspect => Inspect(request),
            SessionStateTransferAction.Capture => Capture(request),
            SessionStateTransferAction.Preview => Prepare(request),
            SessionStateTransferAction.Prepare => Prepare(request),
            SessionStateTransferAction.Activate => Activate(request),
            SessionStateTransferAction.Rollback => Rollback(request),
            SessionStateTransferAction.Recover => Recover(request),
            _ => throw new SessionLaunchException("The requested state-transfer action is unavailable.")
        };
    }

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        Func<SessionStateTransferRequest, SessionStateTransfer>? createTransfer = null)
    {
        string? requestId = null;
        try
        {
            SessionStateTransferRequest request =
                SessionStateTransferProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            string workspace = Path.GetDirectoryName(Path.GetFullPath(requestPath))!;
            if (!string.Equals(workspace, request.WorkspaceDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionLaunchException("The state-transfer workspace does not match its request.");
            }
            SessionStateTransfer transfer = createTransfer?.Invoke(request) ??
                new SessionStateTransfer(
                    AgentProfile.GetPath(),
                    SessionStateAccess.ForAgent().DirectoryPath,
                    new StateArchiveApplication(request));
            SessionStateTransferResult result = transfer.Execute(request);
            writeFile(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionStateTransferProtocol.SerializeResult(result));
            return 0;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or UnauthorizedAccessException or
                System.Text.Json.JsonException or ArgumentException)
        {
            writeFile(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionStateTransferProtocol.SerializeResult(new SessionStateTransferResult
                {
                    RequestId = requestId,
                    Error = exception.Message
                }));
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    private SessionStateTransferResult Capture(SessionStateTransferRequest request)
    {
        using IDisposable worker = _access.EnterWorker();
        StateActivationJournal? journal = request.TransactionId is null ? null : RequireJournal(request);
        using IDisposable state = journal is null ? _access.EnterReader() : _access.EnterMaintenance();
        if (journal is not null && !HasState())
        {
            foreach (StateActivationAsset asset in journal.Assets)
            {
                if (Exists(Path.Combine(_profile, asset.RelativePath)) &&
                    !StateTransferFiles.Covers(".openclaw", asset.RelativePath))
                {
                    throw new SessionLaunchException(
                        "The restore target contains data outside the current OpenClaw inventory. " +
                        "Move it aside explicitly before restoring.");
                }
            }
            SaveJournal(journal with { ProtectionCaptured = true, HadState = false });
            return Result(request) with { HasState = false, Phase = "protected", TransactionId = journal.TransactionId };
        }
        string destination = Path.Combine(
            request.WorkspaceDirectory!,
            $"state-archive-{request.RequestId}.tar.gz");
        StateArchiveApplicationResult captured =
            _application.Capture(_profile, destination, request.DryRun);
        if (journal is not null)
        {
            foreach (StateActivationAsset asset in journal.Assets)
            {
                string target = Path.Combine(_profile, asset.RelativePath);
                if (Exists(target) && !captured.Assets.Any(existing =>
                    StateTransferFiles.Covers(existing.SourcePath, target)))
                {
                    throw new SessionLaunchException(
                        "A restore target is not covered by current-state protection. " +
                        "Move that target aside explicitly before restoring.");
                }
            }
        }
        if (request.DryRun)
        {
            return Result(request) with
            {
                Assets = captured.Assets,
                Warnings = captured.Warnings,
                HasState = HasState()
            };
        }
        if (!string.Equals(Path.GetFullPath(captured.ArchivePath), destination,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException("Upstream wrote the backup outside its requested staging path.");
        }
        using FileStream archive = TrustedPath.OpenRead(
            request.WorkspaceDirectory!, destination, protectContents: true);
        string hash = Convert.ToHexString(SHA256.HashData(archive));
        if (journal is not null)
        {
            SaveJournal(journal with
            {
                ProtectionCaptured = true,
                HadState = true,
                ProtectionSha256 = hash
            });
        }
        return Result(request) with
        {
            Archive = new StateTransferArchive(destination, archive.Length, hash, Verified: true),
            Assets = captured.Assets,
            Warnings = captured.Warnings,
            HasState = true
        };
    }

    private SessionStateTransferResult Inspect(SessionStateTransferRequest request)
    {
        StateActivationJournal? journal = ReadJournal();
        return Result(request) with
        {
            HasState = HasState(),
            Pending = journal is not null,
            TransactionId = journal?.TransactionId,
            Phase = journal?.Phase,
            ProtectionArchive = journal?.ProtectionArchive
        };
    }

    private SessionStateTransferResult Prepare(SessionStateTransferRequest request)
    {
        using IDisposable worker = _access.EnterWorker();
        using IDisposable state = _access.EnterReader();
        string transaction = request.TransactionId ?? Guid.NewGuid().ToString("N");
        string root = TransactionRoot(transaction);
        string extracted = Path.Combine(root, "extracted");
        string prepared = Path.Combine(root, "profile");
        bool retained = false;
        try
        {
            using FileStream archive = VerifyArchive(request);
            Directory.CreateDirectory(root);
            StateArchiveApplicationResult incoming =
                _application.Extract(_profile, request.ArchivePath!, extracted);
            string original = incoming.SourceProfile ??
                throw new SessionLaunchException("The archive does not identify its original profile.");
            List<StateTransferMapping> mappings = BuildMappings(incoming.Assets, original);
            Directory.CreateDirectory(prepared);
            foreach (StateTransferMapping mapping in mappings)
            {
                StateTransferAsset asset = incoming.Assets.Single(item =>
                    item.SourcePath.Equals(mapping.SourcePath, StringComparison.OrdinalIgnoreCase));
                string archiveRelative = asset.ArchivePath.Replace('/', '\\');
                StateTransferFiles.ValidateRelative(archiveRelative);
                string source = Path.Combine(extracted, archiveRelative);
                TrustedPath.EnsureNoReparsePoints(extracted, source);
                string target = Path.Combine(prepared, mapping.ProfileRelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                _move(source, target);
            }
            _ = StateTransferFiles.Entries(prepared);
            ValidateDependencies(prepared, original, _application.RequiredSources(prepared, original));
            _application.RebaseConfiguration(prepared, original, prepared);
            ValidateDependencies(prepared, prepared, _application.RequiredSources(prepared, prepared));
            _application.RebaseConfiguration(prepared, prepared, _profile);
            if (request.Action == SessionStateTransferAction.Preview)
            {
                return Result(request) with { Mappings = mappings, Assets = incoming.Assets, Warnings = incoming.Warnings };
            }
            StateActivationJournal journal = new()
            {
                TransactionId = transaction,
                Profile = _profile,
                ProfileIdentity = Identity(_profile),
                SourceProfile = original,
                PreparedSha256 = StateTransferFiles.Digest(prepared),
                Assets = [.. mappings.Select(mapping => new StateActivationAsset(
                    mapping.ProfileRelativePath,
                    Identity(Path.Combine(prepared, mapping.ProfileRelativePath))))]
            };
            using (IDisposable intent = _access.EnterIntent())
            {
                _access.RequireNoPending();
                SaveJournal(journal);
            }
            retained = true;
            return Result(request) with
            {
                TransactionId = transaction,
                Phase = journal.Phase,
                Pending = true,
                HasState = HasState(),
                Mappings = mappings,
                Assets = incoming.Assets,
                Warnings = incoming.Warnings
            };
        }
        finally
        {
            if (!retained)
            {
                StateTransferFiles.Delete(root);
            }
        }
    }

    private SessionStateTransferResult Activate(SessionStateTransferRequest request)
    {
        using IDisposable worker = _access.EnterWorker();
        StateActivationJournal journal = RequireJournal(request);
        using IDisposable state = _access.EnterMaintenance();
        if (!journal.ProtectionCaptured || (journal.HadState &&
            (request.ProtectionArchive is null || !string.Equals(
                request.ProtectionSha256, journal.ProtectionSha256, StringComparison.OrdinalIgnoreCase))))
        {
            throw new SessionLaunchException("Verified current-state protection must be retained before activation.");
        }
        if (journal.Phase != "prepared")
        {
            throw new SessionLaunchException("Activation was interrupted. Run `clawctl restore --rollback --yes`.");
        }
        string root = TransactionRoot(journal.TransactionId);
        if (!StateTransferFiles.Digest(Path.Combine(root, "profile")).Equals(
            journal.PreparedSha256, StringComparison.Ordinal))
        {
            throw new SessionLaunchException("Prepared state contents changed before activation. Run `clawctl restore --rollback --yes`.");
        }
        journal = journal with { Phase = "activating", ProtectionArchive = request.ProtectionArchive };
        SaveJournal(journal);
        List<StateActivationAsset> assets = [.. journal.Assets];
        for (int index = 0; index < assets.Count; index++)
        {
            StateActivationAsset asset = assets[index];
            string target = Path.Combine(_profile, asset.RelativePath);
            string prepared = Path.Combine(root, "profile", asset.RelativePath);
            string original = Path.Combine(root, "original", asset.RelativePath);
            TrustedPath.EnsureNoReparsePoints(_profile, target);
            if (Identity(prepared) != asset.PreparedIdentity)
            {
                throw new SessionLaunchException("A prepared asset changed before activation.");
            }
            bool exists = Exists(target);
            assets[index] = asset with
            {
                HadOriginal = exists,
                OriginalIdentity = exists ? Identity(target) : null
            };
            journal = journal with { Assets = [.. assets] };
            SaveJournal(journal);
            if (exists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                _move(target, original);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            _move(prepared, target);
        }
        journal = journal with { Phase = "committed" };
        SaveJournal(journal);
        SessionConfigReadinessResult readiness = SessionConfigReadinessChecker.Classify(
            request.RequestId!, Path.Combine(_profile, ".openclaw", "openclaw.json"),
            File.ReadAllText, File.Exists);
        IReadOnlyList<string> cleanupWarnings = ClearJournal(journal);
        return Result(request) with
        {
            TransactionId = journal.TransactionId,
            Phase = "completed",
            ProtectionArchive = journal.ProtectionArchive,
            HasState = HasState(),
            Readiness = readiness,
            Warnings = ["The managed gateway is stopped. Review restored state before `clawctl gateway-service start`.",
                .. cleanupWarnings]
        };
    }

    private SessionStateTransferResult Rollback(SessionStateTransferRequest request)
    {
        using IDisposable worker = _access.EnterWorker();
        StateActivationJournal journal = ReadJournal() ??
            throw new SessionLaunchException("There is no pending activation to roll back.");
        using IDisposable state = _access.EnterMaintenance();
        journal = journal with { Phase = "rolling-back" };
        SaveJournal(journal);
        string root = TransactionRoot(journal.TransactionId);
        foreach (StateActivationAsset asset in journal.Assets.Reverse())
        {
            if (asset.HadOriginal is null)
            {
                continue;
            }
            string target = Path.Combine(_profile, asset.RelativePath);
            string prepared = Path.Combine(root, "profile", asset.RelativePath);
            string original = Path.Combine(root, "original", asset.RelativePath);
            TrustedPath.EnsureNoReparsePoints(_profile, target);
            TrustedPath.FileIdentity? targetIdentity = Exists(target) ? Identity(target) : null;
            if (targetIdentity == asset.PreparedIdentity)
            {
                if (Exists(prepared))
                {
                    throw new SessionLaunchException("Rollback found conflicting prepared data. Preserve it and collect diagnostics.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(prepared)!);
                _move(target, prepared);
                targetIdentity = null;
            }
            if (asset.HadOriginal == true)
            {
                if (Exists(original))
                {
                    if (targetIdentity is not null || Identity(original) != asset.OriginalIdentity)
                    {
                        throw new SessionLaunchException("Rollback found changed original or target data. Preserve it and collect diagnostics.");
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    _move(original, target);
                }
                else if (targetIdentity != asset.OriginalIdentity)
                {
                    throw new SessionLaunchException("Rollback cannot locate the recorded original data.");
                }
            }
            else if (targetIdentity is not null)
            {
                throw new SessionLaunchException("Rollback refuses to replace an unrelated target.");
            }
        }
        IReadOnlyList<string> cleanupWarnings = ClearJournal(journal);
        return Result(request) with
        {
            Phase = "rolled-back",
            HasState = HasState(),
            ProtectionArchive = journal.ProtectionArchive,
            Warnings = ["The activation was rolled back; the managed gateway remains stopped.", .. cleanupWarnings]
        };
    }

    private SessionStateTransferResult Recover(SessionStateTransferRequest request)
    {
        using IDisposable worker = _access.EnterWorker();
        using IDisposable state = _access.EnterReader();
        if (request.SourceProfile!.Equals(_profile, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionLaunchException("Recovery cannot use the current agent profile. Use `clawctl backup` instead.");
        }
        string source = request.SourceDirectory!;
        _ = StateTransferFiles.Relative(request.WorkspaceDirectory!, source);
        string snapshot = Path.Combine(_directory, "recoveries", request.RequestId!);
        try
        {
            if (!StateTransferFiles.Digest(source).Equals(request.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionLaunchException("The offline recovery snapshot changed before normalization.");
            }
            StateTransferFiles.Copy(source, snapshot);
            IReadOnlyList<string> required = _application.RequiredSources(snapshot, request.SourceProfile);
            List<string> missing = [];
            foreach (string path in required)
            {
                string relative = StateTransferFiles.Relative(request.SourceProfile, path);
                StateTransferFiles.ValidateProfileAsset(relative);
                if (!Exists(Path.Combine(snapshot, relative)))
                {
                    missing.Add(path);
                }
            }
            if (missing.Count != 0)
            {
                return Result(request) with { RequiredSources = missing, Phase = "needs-source" };
            }
            if (request.DryRun)
            {
                StateArchiveApplicationResult inventory = _application.Capture(
                    snapshot, Path.Combine(request.WorkspaceDirectory!, $"state-archive-{request.RequestId}.tar.gz"),
                    dryRun: true);
                return Result(request) with
                {
                    Phase = "preview",
                    RequiredSources = required,
                    Assets = inventory.Assets,
                    Warnings = ["The source is readable and profile-contained; dry-run did not publish or activate state."]
                };
            }
            _application.RebaseConfiguration(snapshot, request.SourceProfile, snapshot);
            string destination = Path.Combine(request.WorkspaceDirectory!, $"state-archive-{request.RequestId}.tar.gz");
            StateArchiveApplicationResult captured = _application.Capture(snapshot, destination, dryRun: false);
            if (!Path.GetFullPath(captured.ArchivePath).Equals(destination, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionLaunchException("The normalized recovery archive escaped its requested staging path.");
            }
            using FileStream file = TrustedPath.OpenRead(request.WorkspaceDirectory!, destination, protectContents: true);
            return Result(request) with
            {
                Archive = new StateTransferArchive(destination, file.Length,
                    Convert.ToHexString(SHA256.HashData(file)), Verified: true),
                Assets = captured.Assets,
                Warnings = captured.Warnings
            };
        }
        finally
        {
            StateTransferFiles.Delete(snapshot);
        }
    }

    private static List<StateTransferMapping> BuildMappings(IReadOnlyList<StateTransferAsset> assets, string profile)
    {
        if (!assets.Any(asset => asset.Kind == "state" &&
            asset.SourcePath.Equals(Path.Combine(profile, ".openclaw"), StringComparison.OrdinalIgnoreCase)))
        {
            throw new SessionLaunchException("Activation requires a full profile-contained OpenClaw state archive.");
        }
        List<StateTransferMapping> result = [];
        foreach (StateTransferAsset asset in assets.OrderBy(asset => asset.SourcePath.Length))
        {
            if (asset.Kind is not ("state" or "config" or "credentials" or "workspace" or "agent" or "managed skill"))
            {
                throw new SessionLaunchException("The archive has an unsupported state asset kind.");
            }
            string relative = StateTransferFiles.Relative(profile, asset.SourcePath);
            StateTransferFiles.ValidateProfileAsset(relative);
            if (result.Any(mapping => mapping.ProfileRelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SessionLaunchException("The archive has colliding Windows asset paths.");
            }
            if (!result.Any(mapping => StateTransferFiles.Covers(mapping.ProfileRelativePath, relative)))
            {
                result.Add(new StateTransferMapping(asset.SourcePath, relative));
            }
        }
        return result;
    }

    private static void ValidateDependencies(string staged, string previous, IReadOnlyList<string> required)
    {
        foreach (string path in required)
        {
            string relative = StateTransferFiles.Relative(previous, path);
            StateTransferFiles.ValidateProfileAsset(relative);
            if (!Exists(Path.Combine(staged, relative)))
            {
                throw new SessionLaunchException("The archive is missing a required profile-contained configuration or workspace dependency.");
            }
        }
    }

    private static FileStream VerifyArchive(SessionStateTransferRequest request)
    {
        _ = StateTransferFiles.Relative(request.WorkspaceDirectory!, request.ArchivePath!);
        FileStream archive = TrustedPath.OpenRead(request.WorkspaceDirectory!, request.ArchivePath!, protectContents: true);
        try
        {
            if (!Convert.ToHexString(SHA256.HashData(archive)).Equals(request.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionLaunchException("The staged archive failed its SHA-256 check.");
            }
            archive.Position = 0;
            return archive;
        }
        catch
        {
            archive.Dispose();
            throw;
        }
    }

    private string TransactionRoot(string transaction)
    {
        if (!Guid.TryParseExact(transaction, "N", out _))
        {
            throw new SessionLaunchException("The activation transaction id is invalid.");
        }
        return Path.Combine(_directory, "transactions", transaction);
    }

    private StateActivationJournal RequireJournal(SessionStateTransferRequest request)
    {
        StateActivationJournal journal = ReadJournal() ??
            throw new SessionLaunchException("The prepared activation journal is missing.");
        if (!journal.TransactionId.Equals(request.TransactionId, StringComparison.Ordinal))
        {
            throw new SessionLaunchException("The activation transaction changed. Run `clawctl status`.");
        }
        return journal;
    }

    private StateActivationJournal? ReadJournal()
    {
        string text;
        try
        {
            using FileStream input = TrustedPath.OpenRead(_directory, _access.JournalPath, protectContents: true);
            using var reader = new StreamReader(input);
            text = reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        StateActivationJournal journal = JsonSerializer.Deserialize(
            text, StateActivationJsonContext.Default.StateActivationJournal)
            ?? throw new SessionLaunchException("The activation journal is empty. Preserve it and collect diagnostics.");
        if (journal.SchemaVersion != 1 || journal.Kind != "profile-state-activation" ||
            !string.Equals(journal.Profile, _profile, StringComparison.OrdinalIgnoreCase) ||
            journal.ProfileIdentity != Identity(_profile) ||
            !Path.IsPathFullyQualified(journal.SourceProfile ?? "") ||
            !IsSha256(journal.PreparedSha256) ||
            (journal.HadState && (!journal.ProtectionCaptured || !IsSha256(journal.ProtectionSha256))) ||
            journal.Phase is not ("prepared" or "activating" or "committed" or "rolling-back") ||
            journal.Assets is null || journal.Assets.Count is 0 or > 10000)
        {
            throw new SessionLaunchException("The activation journal is invalid or belongs to another profile. Preserve it and collect diagnostics.");
        }
        _ = TransactionRoot(journal.TransactionId);
        HashSet<string> targets = new(StringComparer.OrdinalIgnoreCase);
        foreach (StateActivationAsset asset in journal.Assets)
        {
            if (asset is null || (asset.HadOriginal == true) != (asset.OriginalIdentity is not null) ||
                (journal.Phase == "prepared" && asset.HadOriginal is not null) ||
                (journal.Phase == "committed" && asset.HadOriginal is null))
            {
                throw new SessionLaunchException("The activation journal has an invalid asset record.");
            }
            StateTransferFiles.ValidateProfileAsset(asset.RelativePath);
            if (!targets.Add(asset.RelativePath) || targets.Any(other =>
                !other.Equals(asset.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                (StateTransferFiles.Covers(other, asset.RelativePath) || StateTransferFiles.Covers(asset.RelativePath, other))))
            {
                throw new SessionLaunchException("The activation journal has colliding target paths.");
            }
        }
        return journal;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private void SaveJournal(StateActivationJournal journal)
    {
        TrustedPath.EnsureNoReparsePoints(Path.GetPathRoot(_directory)!, _directory);
        Directory.CreateDirectory(_directory);
        using TrustedPath.ValidatedDirectory directory =
            TrustedPath.TryOpenValidatedDirectory(_directory, expectedIdentity: null)
            ?? throw new SessionLaunchException("The activation journal directory is unavailable.");
        string temporary = Path.Combine(_directory, $".journal-{Guid.NewGuid():N}.tmp");
        try
        {
            using (Stream output = TrustedPath.CreateNew(directory, temporary))
            {
                byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                    journal, StateActivationJsonContext.Default.StateActivationJournal));
                output.Write(data);
                output.Flush();
            }
            File.Move(temporary, _access.JournalPath, overwrite: true);
        }
        finally
        {
            _ = TrustedPath.TryDeleteOwnedEntry(directory, temporary);
        }
    }

    private IReadOnlyList<string> ClearJournal(StateActivationJournal journal)
    {
        using IDisposable intent = _access.EnterIntent();
        _ = RequireJournal(new SessionStateTransferRequest { TransactionId = journal.TransactionId });
        File.Delete(_access.JournalPath);
        string root = TransactionRoot(journal.TransactionId);
        try
        {
            StateTransferFiles.Delete(root);
            return [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SessionLaunchException)
        {
            return [$"Temporary activation data could not be removed at '{root}'. " +
                "The state operation completed; preserve the retained backup and collect diagnostics before cleaning this directory."];
        }
    }

    private static TrustedPath.FileIdentity Identity(string path) =>
        TrustedPath.TryGetDirectoryIdentity(path) ??
            throw new SessionLaunchException("A recorded state asset is unavailable or redirected.");

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void MoveEntry(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.Directory) != 0)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: false);
        }
    }
    private bool HasState()
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(Path.Combine(_profile, ".openclaw")).Any();
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private SessionStateTransferResult Result(SessionStateTransferRequest request) =>
        new()
        {
            RequestId = request.RequestId,
            ProfileDirectory = _profile
        };
}
