using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// Starts a process from a launch request, preserving the argument vector
/// exactly.
/// </summary>
internal interface ISessionProcessLauncher
{
    /// <summary>Runs the request to completion and returns its exit code.</summary>
    int Run(SessionLaunchRequest request);

    /// <summary>
    /// Starts the request without waiting and returns the identity of the
    /// process that supervises it.
    /// </summary>
    SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath);
}

/// <summary>
/// The supervising process left behind by a detached launch.
/// </summary>
/// <remarks>
/// The creation time is carried with the identifier because Windows reuses
/// process identifiers. An identifier alone would eventually name an unrelated
/// process, which the gateway would then claim and could be asked to stop.
/// </remarks>
internal sealed record SessionDetachedProcess(int ProcessId, DateTimeOffset StartTimeUtc);

internal sealed record SessionProcessOutput(
    int ExitCode,
    string StandardOutput,
    string StandardError);

/// <summary>
/// Launches the requested executable shell-free, so no quoting, metacharacter,
/// or <c>%VAR%</c> interpretation can alter the arguments.
/// </summary>
internal sealed class SessionProcessLauncher(Func<IDisposable>? enterState = null) : ISessionProcessLauncher
{
    private const string NodeOptionsVariable = "NODE_OPTIONS";
    private readonly Func<IDisposable> _enterState = enterState ?? EnterDefaultState;

    public int Run(SessionLaunchRequest request)
    {
        using IDisposable state = _enterState();
        return RunCore(request, capture: false).ExitCode;
    }

    private static IDisposable EnterDefaultState() => SessionStateAccess.ForAgent().EnterReader();

    internal static SessionProcessOutput Capture(SessionLaunchRequest request, TimeSpan? timeout = null) =>
        RunCore(request, capture: true, timeout);

    private static SessionProcessOutput RunCore(SessionLaunchRequest request, bool capture, TimeSpan? timeout = null)
    {
        string workingDirectory = request.WorkingDirectory!;
        if (!Directory.Exists(workingDirectory))
        {
            // Falling back to another directory would run the caller's command
            // somewhere they never asked for, so this is fatal.
            throw new SessionLaunchException(
                $"The requested working directory does not exist: {workingDirectory}");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = request.Executable!,

            // No shell: the isolated session's console handles are inherited.
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = capture
        };

        foreach (string argument in request.Arguments!)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in request.Environment ?? new Dictionary<string, string>())
        {
            startInfo.Environment[name] = value;
        }
        PrependPath(startInfo, Path.GetDirectoryName(request.Executable));

        PrependPath(startInfo, request.PathPrefix);
        AppendNodeOptions(startInfo, request.NodeOptionsSuffix);

        // Held for the launched process's whole lifetime, including anything it
        // starts that inherits the redirect, so setup cannot reclaim the root
        // out from under it.
        using FileStream? lease =
            SessionNativeStager.OpenConsumerLease(request.NativeRootPath);

        try
        {
            if (capture)
            {
                return CaptureInJobAsync(startInfo, timeout).GetAwaiter().GetResult();
            }
            using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
            using Process process = job.StartProcess(startInfo);
            process.WaitForExit();
            return new SessionProcessOutput(process.ExitCode, string.Empty, string.Empty);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            InvalidOperationException or
            PlatformNotSupportedException or
            IOException)
        {
            throw new SessionLaunchException(
                $"Unable to start '{request.Executable}': {exception.Message}");
        }
    }

    private static async Task<SessionProcessOutput> CaptureInJobAsync(
        ProcessStartInfo startInfo,
        TimeSpan? timeout)
    {
        using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
        using var input = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using Process process = job.StartProcess(
            startInfo, input.SafeFileHandle, stdout.ClientSafePipeHandle, stderr.ClientSafePipeHandle);
        stdout.DisposeLocalCopyOfClientHandle();
        stderr.DisposeLocalCopyOfClientHandle();
        using var outputReader = new StreamReader(stdout, Encoding.UTF8);
        using var errorReader = new StreamReader(stderr, Encoding.UTF8);
        Task<string> output = ReadCaptureAsync(outputReader);
        Task<string> error = ReadCaptureAsync(errorReader);
        bool timedOut = false;
        string capturedOutput;
        string capturedError;
        using var budget = new CancellationTokenSource();
        if (timeout is { } limit)
        {
            budget.CancelAfter(limit);
        }
        try
        {
            await process.WaitForExitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            // The state lease cannot end while descendants still have access
            // to the profile. Drain both pipes before disposing their readers.
            job.Dispose();
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    capturedOutput = await output.ConfigureAwait(false);
                }
                finally
                {
                    capturedError = await error.ConfigureAwait(false);
                }
            }
        }
        if (timedOut)
        {
            throw new SessionLaunchException("The packaged state operation did not finish within its execution budget.");
        }
        return new SessionProcessOutput(
            process.ExitCode, capturedOutput, capturedError);
    }

    private static async Task<string> ReadCaptureAsync(StreamReader reader)
    {
        const int maximumCharacters = 16 * 1024 * 1024;
        var result = new StringBuilder();
        char[] buffer = new char[4096];
        bool tooLarge = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > maximumCharacters)
            {
                tooLarge = true;
            }
            if (!tooLarge)
            {
                result.Append(buffer, 0, count);
            }
        }
        if (tooLarge)
        {
            throw new SessionLaunchException("The packaged state operation exceeded the supported metadata-output limit.");
        }
        return result.ToString();
    }

    /// <summary>
    /// Puts a directory at the front of the child's <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the guest can do this. The host supplies environment values that
    /// are merged onto this account's own environment, and it has no way to
    /// know what that account's <c>PATH</c> contains, so it names the directory
    /// and the resolution happens here.
    /// </para>
    /// <para>
    /// Prepending is the point: the packaged runtime has to win over any
    /// machine-wide Node.js for tools that resolve <c>node</c> or <c>npm</c> by
    /// name rather than by the path this package hands them.
    /// </para>
    /// </remarks>
    internal static void PrependPath(ProcessStartInfo startInfo, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        startInfo.Environment.TryGetValue("PATH", out string? inherited);
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(inherited)
            ? directory
            : $"{directory}{Path.PathSeparator}{inherited}";
    }

    /// <summary>
    /// Adds options to the child's own <c>NODE_OPTIONS</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the guest can do this, for the same reason only the guest can
    /// prepend to <c>PATH</c>. The host's environment values are assigned over
    /// this account's own, and the host's <c>NODE_OPTIONS</c> belongs to the
    /// host: composing there would drop whatever the agent set and could push a
    /// host-only preload path into the agent's Node.js processes, which then
    /// fail to start.
    /// </para>
    /// <para>
    /// Appending rather than prepending keeps the agent's own options first,
    /// so this package's preload cannot displace one the agent depends on.
    /// </para>
    /// </remarks>
    internal static void AppendNodeOptions(ProcessStartInfo startInfo, string? options)
    {
        if (string.IsNullOrWhiteSpace(options))
        {
            return;
        }

        startInfo.Environment.TryGetValue(NodeOptionsVariable, out string? inherited);
        startInfo.Environment[NodeOptionsVariable] = string.IsNullOrWhiteSpace(inherited)
            ? options
            : $"{inherited} {options}";
    }

    /// <summary>
    /// Starts a detached launch by re-running this helper as a supervisor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The supervisor exists so that something owns the application's output.
    /// A detached process started directly would inherit the handles of the
    /// execution that launched it, and those close the moment that execution
    /// returns; the application would then be writing into a dead pipe. The
    /// supervisor instead opens the log itself and redirects the application
    /// into it.
    /// </para>
    /// <para>
    /// It is also the process whose identity is recorded. Ownership is then a
    /// claim about a process this package controls, and the listener check can
    /// ask whether the gateway port belongs to it or one of its descendants.
    /// </para>
    /// </remarks>
    public SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath)
    {
        string requestPath = SessionSupervisor.RequestPathFor(request.StatusPath!);
        File.WriteAllText(requestPath, SessionLaunchProtocol.SerializeRequest(request));

        ProcessStartInfo startInfo = new()
        {
            FileName = helperPath,
            UseShellExecute = false,

            // No console of its own: at logon there is no desktop to show it on,
            // and a window would appear over whatever the user is doing.
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory!
        };

        startInfo.ArgumentList.Add("--supervise");
        startInfo.ArgumentList.Add(requestPath);

        Process process;
        try
        {
            using FileStream input = new(
                "NUL",
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using FileStream output = new(
                "NUL",
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            using FileStream error = new(
                "NUL",
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite);
            using WindowsKillOnCloseJob job = WindowsKillOnCloseJob.Create();
            process = job.StartProcess(
                startInfo,
                input.SafeFileHandle,
                output.SafeFileHandle,
                error.SafeFileHandle,
                assignToJob: false);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
            ArgumentException or
            IOException or
            InvalidOperationException or
            PlatformNotSupportedException)
        {
            throw new SessionLaunchException(
                $"Unable to start the gateway supervisor '{helperPath}': " +
                exception.Message);
        }

        using (process)
        {
            try
            {
                return new SessionDetachedProcess(process.Id, process.StartTime.ToUniversalTime());
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                // Without a creation time the process cannot be identified later,
                // and the listener check could claim an unrelated process.
                TryKill(process);
                throw new SessionLaunchException(
                    "The gateway supervisor could not be identified after it " +
                    $"started, so it was stopped: {exception.Message}");
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }
}
