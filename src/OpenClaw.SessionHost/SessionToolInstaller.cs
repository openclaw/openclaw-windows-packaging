using System.Text;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>Installs the agent command shim from inside the isolated account.</summary>
internal static class SessionToolInstaller
{
    private const string DirectoryName = ".openclaw-tools";
    private const string ShimFileName = "openclaw.cmd";
    private const string ShimContent =
        "@echo off\r\n" +
        "setlocal\r\n" +
        "if not defined OPENCLAW_SHIM_NODE goto :missing\r\n" +
        "if not defined OPENCLAW_SHIM_ENTRY goto :missing\r\n" +
        "\"%OPENCLAW_SHIM_NODE%\" \"%OPENCLAW_SHIM_ENTRY%\" %*\r\n" +
        "exit /b %ERRORLEVEL%\r\n" +
        ":missing\r\n" +
        "echo openclaw: this shim only runs inside an OpenClaw agent session.>&2\r\n" +
        "exit /b 9009\r\n";

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;
        try
        {
            SessionToolInstallRequest request =
                SessionRuntimeProtocol.ReadToolInstallRequest(readFile(requestPath));
            requestId = request.RequestId;
            string directory = Path.Combine(request.WorkspacePath!, DirectoryName);
            string shimPath = Path.Combine(directory, ShimFileName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(shimPath, ShimContent, Encoding.ASCII);
            writeFile(
                resultPath,
                SessionRuntimeProtocol.SerializeToolInstallResult(new SessionToolInstallResult
                {
                    RequestId = requestId,
                    ShimPath = shimPath
                }));
            return 0;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or UnauthorizedAccessException)
        {
            try
            {
                writeFile(
                    resultPath,
                    SessionRuntimeProtocol.SerializeToolInstallResult(new SessionToolInstallResult
                    {
                        RequestId = requestId,
                        Error = exception.Message
                    }));
            }
            catch (Exception writeException) when (
                writeException is IOException or UnauthorizedAccessException)
            {
            }

            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }
}
