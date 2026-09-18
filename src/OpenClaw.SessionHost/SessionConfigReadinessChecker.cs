using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>Implements the guest-side file-only OpenClaw config check.</summary>
internal static class SessionConfigReadinessChecker
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile,
        string? profileRoot = null,
        Func<string, bool>? fileExists = null)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        string? requestId = null;

        try
        {
            SessionConfigReadinessRequest request =
                SessionConfigReadinessProtocol.ReadRequest(readFile(requestPath));
            requestId = request.RequestId!;
            string profile = profileRoot ?? AgentProfile.GetPath();
            string configPath = Path.Combine(profile, ".openclaw", "openclaw.json");
            SessionConfigReadinessResult result = Classify(
                requestId,
                configPath,
                readFile,
                fileExists ?? File.Exists);
            writeFile(resultPath, SessionConfigReadinessProtocol.SerializeResult(result));
            return 0;
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            try
            {
                writeFile(
                    resultPath,
                    SessionConfigReadinessProtocol.SerializeResult(
                        new SessionConfigReadinessResult
                        {
                            RequestId = requestId,
                            Error = exception.Message
                        }));
            }
            catch (Exception writeException) when (
                writeException is IOException or UnauthorizedAccessException)
            {
                // The helper exit code remains the only available signal.
            }

            return SessionLaunchProtocol.HelperFailureExitCode;
        }
    }

    internal static SessionConfigReadinessResult Classify(
        string requestId,
        string configPath,
        Func<string, string> readFile,
        Func<string, bool> fileExists)
    {
        if (!fileExists(configPath))
        {
            return Result(
                requestId,
                SessionConfigReadinessState.Absent,
                SessionConfigReadinessReason.ConfigFileMissing);
        }

        string json;
        try
        {
            json = readFile(configPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Result(
                requestId,
                SessionConfigReadinessState.Absent,
                SessionConfigReadinessReason.ConfigFileMissing);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Result(
                requestId,
                SessionConfigReadinessState.NotReady,
                SessionConfigReadinessReason.ConfigFileUnreadable);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json, DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Result(
                    requestId,
                    SessionConfigReadinessState.NotReady,
                    SessionConfigReadinessReason.ConfigFileInvalid);
            }

            if (!document.RootElement.TryGetProperty("gateway", out JsonElement gateway) ||
                gateway.ValueKind != JsonValueKind.Object)
            {
                return Result(
                    requestId,
                    SessionConfigReadinessState.NotReady,
                    SessionConfigReadinessReason.GatewayMissing);
            }

            if (!gateway.TryGetProperty("mode", out JsonElement mode) ||
                mode.ValueKind != JsonValueKind.String)
            {
                return Result(
                    requestId,
                    SessionConfigReadinessState.NotReady,
                    SessionConfigReadinessReason.GatewayModeMissing);
            }

            return string.Equals(mode.GetString(), "local", StringComparison.Ordinal)
                ? Result(
                    requestId,
                    SessionConfigReadinessState.StartupEligible,
                    SessionConfigReadinessReason.GatewayModeLocal)
                : Result(
                    requestId,
                    SessionConfigReadinessState.NotReady,
                    SessionConfigReadinessReason.GatewayModeNotLocal);
        }
        catch (JsonException)
        {
            return Result(
                requestId,
                SessionConfigReadinessState.NotReady,
                SessionConfigReadinessReason.ConfigFileInvalid);
        }
    }

    private static SessionConfigReadinessResult Result(
        string requestId,
        SessionConfigReadinessState state,
        SessionConfigReadinessReason reason) =>
        new()
        {
            RequestId = requestId,
            State = state,
            Reason = reason
        };
}
