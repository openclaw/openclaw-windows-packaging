using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenClaw.Launcher;

internal sealed record PayloadStartupList(int SchemaVersion, IReadOnlyList<string>? Files);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PayloadStartupList))]
internal sealed partial class PayloadStartupListJsonContext : JsonSerializerContext;

internal sealed record PayloadPrewarmLimits(int MaximumFiles, long MaximumBytes, TimeSpan Deadline)
{
    public static PayloadPrewarmLimits Default { get; } =
        new(8000, 256L * 1024 * 1024, TimeSpan.FromMinutes(5));
}

internal enum PayloadPrewarmOutcome
{
    Completed,
    MissingList,
    InvalidList,
    Cancelled,
    DeadlineReached,
    ByteLimitReached,
    FileLimitReached,
    BackgroundModeUnavailable,
}

internal sealed record PayloadPrewarmResult(
    PayloadPrewarmOutcome Outcome,
    int FilesRead,
    int FilesSkipped,
    long BytesRead,
    TimeSpan Elapsed)
{
    public string? Detail { get; init; }

    public string Describe() => Outcome == PayloadPrewarmOutcome.BackgroundModeUnavailable
        ? $"Payload prewarm skipped: background mode could not be entered ({Detail}); no files were read."
        : $"Payload prewarm {Outcome}: read {FilesRead} files ({BytesRead} bytes), " +
          $"skipped {FilesSkipped}, in {Elapsed.TotalSeconds:0.0} s.";
}

/// <summary>
/// Reads the packaged startup file list once, at background priority, after
/// setup succeeds.
/// </summary>
/// <remarks>
/// The intent is to let on-access antivirus scan, and possibly cache verdicts
/// for, the files the Gateway's first start opens. Whether a read by the
/// invoking user benefits the isolated agent identity's later read is not
/// established, and the list only approximates startup, so the pass is a hint:
/// it never fails setup, reads only inventoried package files under the
/// application directory, and stops at the first exhausted budget.
/// </remarks>
internal static class PayloadPrewarm
{
    public const string CommandName = "payload-prewarm";
    private const int BufferSize = 64 * 1024;
    private const string InstanceMutexPrefix = @"Local\OpenClaw.PayloadPrewarm.";
    private const string CancelEventPrefix = @"Local\OpenClaw.PayloadPrewarm.Cancel.";

    public static string GetListPath(string baseDirectory) =>
        Path.Combine(baseDirectory, "payload", "startup-files.json");

    public static PayloadPrewarmResult Run(
        string listPath,
        string applicationDirectory,
        PayloadPrewarmLimits limits,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(clock);

        long started = clock.GetTimestamp();
        int read = 0;
        int skipped = 0;
        long bytes = 0;
        PayloadPrewarmResult Finish(PayloadPrewarmOutcome outcome) =>
            new(outcome, read, skipped, bytes, clock.GetElapsedTime(started));

        PayloadStartupList? list;
        try
        {
            using FileStream stream = File.OpenRead(listPath);
            list = JsonSerializer.Deserialize(
                stream,
                PayloadStartupListJsonContext.Default.PayloadStartupList);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Finish(PayloadPrewarmOutcome.MissingList);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return Finish(PayloadPrewarmOutcome.InvalidList);
        }

        if (list is not { SchemaVersion: 1, Files: not null })
        {
            return Finish(PayloadPrewarmOutcome.InvalidList);
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)) +
            Path.DirectorySeparatorChar;
        byte[] buffer = new byte[BufferSize];
        foreach (string? relativePath in list.Files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Finish(PayloadPrewarmOutcome.Cancelled);
            }

            if (clock.GetElapsedTime(started) >= limits.Deadline)
            {
                return Finish(PayloadPrewarmOutcome.DeadlineReached);
            }

            if (read >= limits.MaximumFiles)
            {
                return Finish(PayloadPrewarmOutcome.FileLimitReached);
            }

            string? path = ResolveContainedPath(root, relativePath);
            if (path is null)
            {
                skipped++;
                continue;
            }

            try
            {
                using FileStream file = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    BufferSize,
                    FileOptions.SequentialScan);
                int count;
                while ((count = file.Read(buffer, 0, buffer.Length)) > 0)
                {
                    bytes += count;
                    if (bytes >= limits.MaximumBytes)
                    {
                        read++;
                        return Finish(PayloadPrewarmOutcome.ByteLimitReached);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Finish(PayloadPrewarmOutcome.Cancelled);
                    }

                    if (clock.GetElapsedTime(started) >= limits.Deadline)
                    {
                        return Finish(PayloadPrewarmOutcome.DeadlineReached);
                    }
                }

                read++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                skipped++;
            }
        }

        return Finish(PayloadPrewarmOutcome.Completed);
    }

    // List entries are '/'-separated paths relative to app/. Anything that is
    // not a plain relative path, or that resolves outside the root, is skipped.
    internal static string? ResolveContainedPath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith('/') ||
            relativePath.Contains('\\', StringComparison.Ordinal) ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split('/').Any(static segment => segment is "" or "." or ".."))
        {
            return null;
        }

        string path = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>
    /// Runs the hidden <c>clawctl payload-prewarm</c> command. It always
    /// succeeds; the outcome is recorded only in the host diagnostic log.
    /// </summary>
    public static int RunWorker(
        HostOptions options,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        using ITimer lifetimeBound = ArmLifetimeBound(
            TimeProvider.System,
            PayloadPrewarmLimits.Default.Deadline + LifetimeGrace,
            LifetimeLogGrace,
            log,
            PayloadPrewarmNative.TerminateCurrentProcess);
        try
        {
            string scope = GetScope();
            using Mutex instance = new(initiallyOwned: false, InstanceMutexPrefix + scope);
            bool owned;
            try
            {
                owned = instance.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                owned = true;
            }

            if (!owned)
            {
                log("Payload prewarm is already running for this installation.");
                return 0;
            }

            try
            {
                using EventWaitHandle cancelSignal = new(
                    initialState: false,
                    EventResetMode.ManualReset,
                    CancelEventPrefix + scope);
                using CancellationTokenSource linked =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                    cancelSignal,
                    static (state, _) => ((CancellationTokenSource)state!).Cancel(),
                    linked,
                    Timeout.Infinite,
                    executeOnlyOnce: true);
                try
                {
                    _ = RunPass(
                        PayloadPrewarmNative.TryEnterBackgroundMode,
                        GetListPath(AppContext.BaseDirectory),
                        options.RequirePackagedApplicationDirectory(),
                        PayloadPrewarmLimits.Default,
                        TimeProvider.System,
                        log,
                        linked.Token);
                }
                finally
                {
                    _ = registration.Unregister(null);
                }
            }
            finally
            {
                instance.ReleaseMutex();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log($"Payload prewarm stopped: {DiagnosticFailure.Describe(exception)}");
        }

        return 0;
    }

    // The pass checks its deadline before each file and after each chunk, but a
    // blocked open or read cannot observe it. The grace lets the cooperative
    // stop win normally; the bound is what limits the worker's lifetime.
    internal static TimeSpan LifetimeGrace { get; } = TimeSpan.FromSeconds(15);

    internal static TimeSpan LifetimeLogGrace { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Ends the dedicated worker process once <paramref name="limit"/> elapses,
    /// even if a file open or read is blocked.
    /// </summary>
    /// <remarks>
    /// The diagnostic write runs on its own thread and gets at most
    /// <paramref name="logGrace"/>, because the log can block too; termination
    /// follows whether or not it finished. Only the worker arms this, so it
    /// never ends setup or any other process.
    /// </remarks>
    internal static ITimer ArmLifetimeBound(
        TimeProvider clock,
        TimeSpan limit,
        TimeSpan logGrace,
        Action<string> log,
        Action terminate)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(terminate);

        string message =
            $"Payload prewarm reached its {limit.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} s " +
            "lifetime bound; ending the worker.";
        return clock.CreateTimer(
            _ =>
            {
                try
                {
                    _ = Task.Run(() => log(message)).Wait(logGrace);
                }
                catch (AggregateException)
                {
                }
                finally
                {
                    terminate();
                }
            },
            null,
            limit,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Enters background resource scheduling, then reads the list. Priority
    /// class alone does not lower file I/O, so a worker that cannot enter
    /// background mode reads nothing and records why.
    /// </summary>
    internal static PayloadPrewarmResult RunPass(
        Func<int?> enterBackgroundMode,
        string listPath,
        string applicationDirectory,
        PayloadPrewarmLimits limits,
        TimeProvider clock,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(enterBackgroundMode);
        ArgumentNullException.ThrowIfNull(log);

        PayloadPrewarmResult result = enterBackgroundMode() is int error
            ? new(PayloadPrewarmOutcome.BackgroundModeUnavailable, 0, 0, 0, TimeSpan.Zero)
            {
                Detail = $"Win32 error {error.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            }
            : Run(listPath, applicationDirectory, limits, clock, cancellationToken);
        log(result.Describe());
        return result;
    }

    /// <summary>
    /// Asks a running prewarm for this installation to stop. Teardown does
    /// not wait: the worker only reads package files.
    /// </summary>
    public static void SignalCancel(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            if (EventWaitHandle.TryOpenExisting(CancelEventPrefix + GetScope(), out EventWaitHandle? signal))
            {
                using (signal)
                {
                    _ = signal.Set();
                }

                log("Asked the running payload prewarm to stop.");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            log($"The payload prewarm stop signal could not be sent: {DiagnosticFailure.Describe(exception)}");
        }
    }

    // Side-by-side development identities and checkouts each prewarm their own
    // payload, so named objects are scoped to the package family or directory.
    private static string GetScope() =>
        HostDataPaths.TryGetPackageFamilyName() ??
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            AppContext.BaseDirectory.ToUpperInvariant())))[..16];

    /// <summary>
    /// Starts the worker through the package's own <c>clawctl</c> alias after
    /// setup, unless the operator opted out. Every failure is logged and
    /// swallowed so setup's result never depends on the prewarm.
    /// </summary>
    public static void TryStart(
        Func<string, string?> readEnvironmentVariable,
        Func<string?> getPackageFamilyName,
        string baseDirectory,
        string localApplicationData,
        Func<string, int> startDetached,
        Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(readEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(getPackageFamilyName);
        ArgumentNullException.ThrowIfNull(startDetached);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            if (!Gateway.AutoBehaviorPolicy.IsPayloadPrewarmEnabled(readEnvironmentVariable))
            {
                log($"Payload prewarm is disabled by {OpenClawRuntimeEnvironment.PayloadPrewarmVariable}.");
                return;
            }

            string? packageFamilyName = getPackageFamilyName();
            if (packageFamilyName is null)
            {
                log("Payload prewarm skipped: the host has no package identity.");
                return;
            }

            if (!File.Exists(GetListPath(baseDirectory)))
            {
                log("Payload prewarm skipped: the package has no startup file list.");
                return;
            }

            if (!PackageExecutionAlias.TryResolve(
                    baseDirectory,
                    packageFamilyName,
                    "Control",
                    "openclaw.exe",
                    localApplicationData,
                    out string? aliasPath,
                    out string? error))
            {
                log($"Payload prewarm skipped: {error}");
                return;
            }

            int processId = startDetached(aliasPath);
            log($"Started payload prewarm process {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log($"Payload prewarm could not start: {DiagnosticFailure.Describe(exception)}");
        }
    }

    public static void TryStart(Action<string> log) =>
        TryStart(
            Environment.GetEnvironmentVariable,
            HostDataPaths.TryGetPackageFamilyName,
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            aliasPath => PayloadPrewarmNative.StartDetached(aliasPath, CommandName),
            log);
}
