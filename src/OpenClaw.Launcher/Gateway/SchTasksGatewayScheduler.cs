using System.Diagnostics;
using System.Text;

namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// Drives Task Scheduler through the inbox <c>schtasks.exe</c>.
/// </summary>
/// <remarks>
/// <para>
/// The COM Task Scheduler API is not usable here: this launcher is published
/// with NativeAOT, and the interop that API needs is exactly what NativeAOT
/// cannot generate. The inbox executable is present on every supported Windows
/// installation and takes the same structured task XML.
/// </para>
/// <para>
/// Nothing in this type elevates. Registration must run as the signed-in user:
/// a task registered elevated is owned by <c>BUILTIN\Administrators</c>, and
/// the user can then neither overwrite nor delete their own gateway task.
/// </para>
/// </remarks>
internal sealed class SchTasksGatewayScheduler : IGatewayTaskScheduler
{
    private readonly string _executablePath;
    private readonly Func<string[], CancellationToken, Task<SchTasksOutcome>>? _run;

    public SchTasksGatewayScheduler()
        : this(ResolveDefaultExecutablePath())
    {
    }

    internal SchTasksGatewayScheduler(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
    }

    // Tests drive classification without launching schtasks.exe, which must
    // never register, run, or delete a real task on a developer's machine.
    internal SchTasksGatewayScheduler(
        Func<string[], CancellationToken, Task<SchTasksOutcome>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _executablePath = "schtasks.exe";
        _run = run;
    }

    public async Task<GatewayTaskProbe> QueryAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        SchTasksOutcome outcome;
        try
        {
            outcome = await RunAsync(
                ["/Query", "/TN", taskName, "/XML", "ONE"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (SchTasksLaunchException exception)
        {
            return GatewayTaskProbe.Unreadable(exception.Message);
        }

        if (outcome.ExitCode != 0)
        {
            // "Not found" and "refused" must not collapse into one answer. A
            // caller that re-registers on a refused read retries forever: the
            // write is as likely to be refused as the read was.
            return await ClassifyQueryFailureAsync(taskName, outcome, cancellationToken)
                .ConfigureAwait(false);
        }

        return GatewayTaskDefinition.TryParse(
            outcome.StandardOutput,
            out GatewayTaskSnapshot? snapshot,
            out string? detail)
            ? GatewayTaskProbe.Present(snapshot!)
            : GatewayTaskProbe.Unreadable(
                $"The registered task definition could not be read: {detail}");
    }

    public async Task<GatewayTaskOperation> RegisterAsync(
        string taskName,
        string taskXml,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);
        ArgumentNullException.ThrowIfNull(taskXml);

        string xmlPath = Path.Combine(
            Path.GetTempPath(),
            $"openclaw-gateway-task-{Guid.NewGuid():n}.xml");

        try
        {
            // The definition declares UTF-16, so the file has to be UTF-16 with
            // a byte-order mark or schtasks rejects it.
            await File.WriteAllTextAsync(
                xmlPath,
                taskXml,
                new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
                cancellationToken).ConfigureAwait(false);

            SchTasksOutcome outcome = await RunAsync(
                ["/Create", "/TN", taskName, "/XML", xmlPath, "/F"],
                cancellationToken).ConfigureAwait(false);

            return outcome.ExitCode == 0
                ? GatewayTaskOperation.Success
                : GatewayTaskOperation.Failure(Describe(outcome));
        }
        catch (SchTasksLaunchException exception)
        {
            return GatewayTaskOperation.Failure(exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return GatewayTaskOperation.Failure(
                $"The task definition could not be staged at '{xmlPath}': " +
                exception.Message);
        }
        finally
        {
            TryDelete(xmlPath);
        }
    }

    public async Task<GatewayTaskOperation> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        SchTasksOutcome outcome;
        try
        {
            outcome = await RunAsync(
                ["/Delete", "/TN", taskName, "/F"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (SchTasksLaunchException exception)
        {
            return GatewayTaskOperation.Failure(exception.Message);
        }

        // Deleting what is already gone is the requested end state.
        if (outcome.ExitCode == 0)
        {
            return GatewayTaskOperation.Success;
        }

        return await IsAbsentAsync(taskName, cancellationToken).ConfigureAwait(false)
            ? GatewayTaskOperation.Success
            : GatewayTaskOperation.Failure(Describe(outcome));
    }

    public async Task<GatewayTaskOperation> RunAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        SchTasksOutcome outcome;
        try
        {
            outcome = await RunAsync(["/Run", "/TN", taskName], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SchTasksLaunchException exception)
        {
            return GatewayTaskOperation.Failure(exception.Message);
        }

        return outcome.ExitCode == 0
            ? GatewayTaskOperation.Success
            : GatewayTaskOperation.Failure(Describe(outcome));
    }

    private static string ResolveDefaultExecutablePath()
    {
        // An absolute path to the inbox executable, so a same-named program
        // earlier on PATH can never be the one that registers a logon task.
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrEmpty(system)
            ? "schtasks.exe"
            : Path.Combine(system, "schtasks.exe");
    }

    /// <summary>
    /// Separates a missing task from a refused one without reading localized
    /// diagnostics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>schtasks.exe</c> writes its errors in the console's display language
    /// and returns exit code 1 for both "no such task" and "access denied", so
    /// neither the text nor the exit code can classify the failure. Matching
    /// English phrases made every absent task look unreadable on a localized
    /// Windows installation, which suppressed both registration and the
    /// Startup-folder fallback and left setup unable to reach Ready.
    /// </para>
    /// <para>
    /// Enumeration answers the same question structurally. The listing reports
    /// the task names this account can see, and the name being looked for is
    /// one this package generated, so the comparison is ordinal and carries no
    /// language. A successful listing that omits the name proves absence; a
    /// listing that contains it proves the earlier read was refused rather
    /// than empty; a listing that fails leaves the question open.
    /// </para>
    /// </remarks>
    private async Task<GatewayTaskProbe> ClassifyQueryFailureAsync(
        string taskName,
        SchTasksOutcome queryOutcome,
        CancellationToken cancellationToken)
    {
        SchTasksOutcome listing;
        try
        {
            listing = await RunAsync(
                ["/Query", "/FO", "CSV", "/NH"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (SchTasksLaunchException exception)
        {
            return GatewayTaskProbe.Unreadable(
                Combine(Describe(queryOutcome), exception.Message));
        }

        if (listing.ExitCode != 0)
        {
            return GatewayTaskProbe.Unreadable(
                Combine(Describe(queryOutcome), Describe(listing)));
        }

        return ListingContains(listing.StandardOutput, taskName)
            ? GatewayTaskProbe.Unreadable(Describe(queryOutcome))
            : GatewayTaskProbe.Missing;
    }

    /// <summary>
    /// Reports whether this account can see that the task is gone.
    /// </summary>
    /// <remarks>
    /// Answers false when the listing itself fails, because an unanswerable
    /// question is not evidence of absence.
    /// </remarks>
    private async Task<bool> IsAbsentAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        SchTasksOutcome listing;
        try
        {
            listing = await RunAsync(
                ["/Query", "/FO", "CSV", "/NH"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (SchTasksLaunchException)
        {
            return false;
        }

        return listing.ExitCode == 0 &&
               !ListingContains(listing.StandardOutput, taskName);
    }

    /// <summary>
    /// Reports whether the CSV listing names this task.
    /// </summary>
    /// <remarks>
    /// Only the first column is considered. The remaining columns carry the
    /// next run time and a localized status, neither of which identifies a
    /// task. Task Scheduler reports names as absolute paths, so a caller's
    /// leading separator is optional.
    /// </remarks>
    internal static bool ListingContains(string listing, string taskName)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskName);

        string wanted = taskName.TrimStart('\\');
        foreach (string line in listing.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('"'))
            {
                continue;
            }

            int closing = line.IndexOf('"', 1);
            if (closing <= 1)
            {
                continue;
            }

            string name = line[1..closing].TrimStart('\\');
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Combine(string first, string second) =>
        string.IsNullOrWhiteSpace(second) ? first : $"{first} {second}";

    private static string Describe(SchTasksOutcome outcome)
    {
        string message = FirstMeaningfulLine(outcome.StandardError)
            ?? FirstMeaningfulLine(outcome.StandardOutput)
            ?? "schtasks.exe reported no diagnostics.";
        return $"schtasks.exe exited with code {outcome.ExitCode}. {message}";
    }

    private static string? FirstMeaningfulLine(string text)
    {
        foreach (string line in text.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            return line;
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is not worth failing a registration
            // that already succeeded.
        }
    }

    private async Task<SchTasksOutcome> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_run is not null)
        {
            return await _run(arguments, cancellationToken).ConfigureAwait(false);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // schtasks uses the console output code page when redirected.
            StandardOutputEncoding = Console.OutputEncoding,
            StandardErrorEncoding = Console.OutputEncoding
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            InvalidOperationException)
        {
            throw new SchTasksLaunchException(
                $"'{_executablePath}' could not be started: {exception.Message}",
                exception);
        }

        // Both streams are drained concurrently so a full pipe buffer on one
        // cannot block the child before it exits.
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        return new SchTasksOutcome(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    internal sealed record SchTasksOutcome(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class SchTasksLaunchException : Exception
    {
        public SchTasksLaunchException()
        {
        }

        public SchTasksLaunchException(string message)
            : base(message)
        {
        }

        public SchTasksLaunchException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
