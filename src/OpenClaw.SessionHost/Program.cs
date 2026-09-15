using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The guest-side helper that runs inside an isolated session.
/// </summary>
/// <remarks>
/// It exists because the backend's execution API takes a command-line string
/// that is flattened through <c>cmd.exe</c>. The MXC command therefore launches
/// only this controlled helper, which reads the real argument vector as data
/// and replays it byte for byte.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args) =>
        Run(args, new SessionProcessLauncher(), Console.Error, File.ReadAllText, WriteResult);

    internal static int Run(
        IReadOnlyList<string> args,
        ISessionProcessLauncher launcher,
        TextWriter errorOutput,
        Func<string, string> readFile,
        Action<string, SessionLaunchResult> writeResult)
    {
        if (!TryGetMode(args, out string? mode, out string? requestPath))
        {
            // No request path means no control file to report through, so this
            // is the one failure that can only surface on stderr.
            errorOutput.WriteLine(
                "openclaw-session-host: usage: openclaw-session-host " +
                "--request|--supervise|--inspect|--stop|--install-runtime|--install-tools <path>");
            return SessionLaunchProtocol.HelperFailureExitCode;
        }

        switch (mode)
        {
            case "--supervise":
                return SessionSupervisor.Run(requestPath, readFile, File.WriteAllText);
            case "--inspect":
                return SessionInspector.Run(requestPath, readFile, File.WriteAllText);
            case "--stop":
                return SessionTerminator.Run(requestPath, readFile, File.WriteAllText);
            case "--install-runtime":
                return SessionRuntimeInstaller.Run(requestPath, readFile, File.WriteAllText);
            case "--install-tools":
                return SessionToolInstaller.Run(requestPath, readFile, File.WriteAllText);
        }

        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;

        try
        {
            SessionLaunchRequest request =
                SessionLaunchProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            if (request.Mode is not SessionLaunchMode.Attached and
                not SessionLaunchMode.Detached)
            {
                throw new SessionLaunchException(
                    $"Launch mode '{request.Mode}' is not supported by this helper.");
            }

            if (request.Mode == SessionLaunchMode.Detached)
            {
                SessionDetachedProcess detached = launcher.Start(
                    request,
                    System.Environment.ProcessPath
                        ?? throw new SessionLaunchException(
                            "The session helper cannot determine its own path, " +
                            "so it cannot start a supervised process."));

                writeResult(
                    resultPath,
                    new SessionLaunchResult
                    {
                        RequestId = requestId,
                        Launched = true,
                        ProcessId = detached.ProcessId,
                        ProcessStartTimeUtc = detached.StartTimeUtc
                    });
                return 0;
            }

            int exitCode = launcher.Run(request);
            writeResult(
                resultPath,
                new SessionLaunchResult
                {
                    RequestId = requestId,
                    Launched = true,
                    ExitCode = exitCode
                });
            return exitCode;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            errorOutput.WriteLine($"openclaw-session-host: {exception.Message}");
            TryWriteFailure(writeResult, resultPath, requestId, exception.Message, errorOutput);
            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    private static bool TryGetMode(
        IReadOnlyList<string> args,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? mode,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? requestPath)
    {
        mode = null;
        requestPath = null;

        // Exactly one option and one path. The helper must never grow into a
        // general-purpose runner reachable from inside the session.
        if (args.Count != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            return false;
        }

        if (args[0] is not ("--request" or "--supervise" or "--inspect" or "--stop" or
            "--collect" or "--install-runtime"))
        {
            return false;
        }

        mode = args[0];
        requestPath = args[1];
        return true;
    }

    private static void TryWriteFailure(
        Action<string, SessionLaunchResult> writeResult,
        string resultPath,
        string? requestId,
        string error,
        TextWriter errorOutput)
    {
        try
        {
            writeResult(
                resultPath,
                new SessionLaunchResult
                {
                    RequestId = requestId,
                    Launched = false,
                    Error = error
                });
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            errorOutput.WriteLine(
                $"openclaw-session-host: unable to record the failure: {exception.Message}");
        }
    }

    private static void WriteResult(string path, SessionLaunchResult result) =>
        File.WriteAllText(path, SessionLaunchProtocol.SerializeResult(result));
}
