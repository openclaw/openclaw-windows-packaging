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
        if (args.Count == 2 && args[0] == "--install-runtime")
        {
            return SessionRuntimeInstaller.Run(
                args[1],
                readFile,
                File.WriteAllText);
        }

        if (args.Count == 2 && args[0] == "--install-tools")
        {
            return SessionToolInstaller.Run(
                args[1],
                readFile,
                File.WriteAllText);
        }

        if (args.Count != 2 || args[0] != "--request")
        {
            // No request path means no control file to report through, so this
            // is the one failure that can only surface on stderr.
            errorOutput.WriteLine(
                "openclaw-session-host: usage: openclaw-session-host " +
                "--request|--install-runtime|--install-tools <path>");
            return SessionLaunchProtocol.HelperFailureExitCode;
        }

        string requestPath = args[1];

        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;

        try
        {
            SessionLaunchRequest request =
                SessionLaunchProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId;
            if (request.Mode != SessionLaunchMode.Attached)
            {
                throw new SessionLaunchException(
                    $"Launch mode '{request.Mode}' is not supported by this helper.");
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
