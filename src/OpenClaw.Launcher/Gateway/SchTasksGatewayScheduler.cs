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

    public SchTasksGatewayScheduler()
        : this(ResolveDefaultExecutablePath())
    {
    }

    internal SchTasksGatewayScheduler(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        _executablePath = executablePath;
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
            return LooksLikeTaskNotFound(outcome)
                ? GatewayTaskProbe.Missing
                : GatewayTaskProbe.Unreadable(Describe(outcome));
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
        return outcome.ExitCode == 0 || LooksLikeTaskNotFound(outcome)
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

    private static bool LooksLikeTaskNotFound(SchTasksOutcome outcome)
    {
        string combined = outcome.StandardOutput + outcome.StandardError;
        return combined.Contains(
                   "cannot find the file specified",
                   StringComparison.OrdinalIgnoreCase) ||
               combined.Contains(
                   "cannot find the task",
                   StringComparison.OrdinalIgnoreCase) ||
               combined.Contains(
                   "does not exist",
                   StringComparison.OrdinalIgnoreCase);
    }

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
        ProcessStartInfo startInfo = new()
        {
            FileName = _executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // schtasks writes its XML in the console output code page even
            // though the declaration claims UTF-16. Every element this code
            // compares is ASCII apart from the launcher path, so a code-page
            // mismatch can at worst be reported as drift; it can never be
            // mistaken for a missing task.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
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
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new SchTasksOutcome(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private sealed record SchTasksOutcome(
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
