using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Launcher.Gateway;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.AotSmoke;

internal static partial class SmokeProgram
{
    // The package inventory reader, the admission envelope, and the typed
    // refusal all cross the trimmed helper boundary, where a dropped
    // serialization member would silently admit or misreport a launch.
    private static Task NativeStageAdmissionWorks()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"clawctl-aot-native-{Guid.NewGuid():N}");
        try
        {
            string application = Path.Combine(directory, "package", "app");
            string addon = Path.Combine(application, "node_modules", "koffi", "koffi.node");
            Directory.CreateDirectory(Path.GetDirectoryName(addon)!);
            File.WriteAllText(addon, "native");
            string inventory = Path.Combine(directory, "package", "payload", "payload-files.json");
            Directory.CreateDirectory(Path.GetDirectoryName(inventory)!);
            File.WriteAllText(
                inventory,
                "{\"files\":[{\"path\":\"node_modules/koffi/koffi.node\",\"length\":6,\"sha256\":\"" +
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("native"))) + "\"}]}");
            string root = SessionNativeStager.Stage(application, Path.Combine(directory, "local"))
                ?? throw new InvalidOperationException("The inventoried native package was not staged.");

            var launcher = new AdmissionRecordingLauncher();
            SessionLaunchResult refused = RunAdmission(directory, launcher, application, rootPath: null);
            Assert(!refused.Launched && launcher.Requests.Count == 0 &&
                refused.ErrorCode == SessionLaunchProtocol.NativeStageStaleErrorCode,
                "A missing native stage was not refused with its typed code under NativeAOT.");

            SessionLaunchResult admitted = RunAdmission(directory, launcher, application, root);
            Assert(admitted.Launched && launcher.Requests.Count == 1 &&
                launcher.Requests[0].NativeStage?.RootPath == root &&
                launcher.Requests[0].NativeStage?.ApplicationDirectory == application,
                "A verified native stage did not reach the launch under NativeAOT.");

            // The check a restart runs before stopping a working Gateway.
            SessionLaunchResult checkRefused = RunAdmission(
                directory, launcher, application, rootPath: null, SessionLaunchMode.Admission);
            SessionLaunchResult checkAdmitted = RunAdmission(
                directory, launcher, application, root, SessionLaunchMode.Admission);
            Assert(checkRefused.ErrorCode == SessionLaunchProtocol.NativeStageStaleErrorCode &&
                checkAdmitted is { Launched: false, Error: null, ErrorCode: null } &&
                checkAdmitted.RequestId == "aot-native-stage" && launcher.Requests.Count == 1,
                "The admission-only check did not decide without launching under NativeAOT.");

            string inspectPath = Path.Combine(directory, "native-stage.json");
            File.WriteAllText(inspectPath, SessionNativeStageProtocol.SerializeRequest(new SessionNativeStageInspectRequest
            {
                RequestId = "aot-native-inspect",
                ApplicationDirectory = application,
                RootPath = root
            }));
            Assert(OpenClaw.SessionHost.Program.Run(
                    ["--inspect-native", inspectPath],
                    launcher,
                    TextWriter.Null,
                    File.ReadAllText,
                    OpenClaw.SessionHost.Program.WriteResult) == 0,
                "Native stage inspection failed under NativeAOT.");
            SessionNativeStageReport report = SessionNativeStageProtocol.ReadReport(
                File.ReadAllText(SessionLaunchProtocol.ResultPathFor(inspectPath)), "aot-native-inspect");
            using var statusOutput = new StringWriter();
            ClawCtlJson.WriteResult(statusOutput, new StatusCommandResult(
                new SessionStatus(SessionAvailability.Running, null, null, null),
                new GatewayStatusReport(GatewayState.NotStarted, null, "No gateway has been started."),
                new GatewayPersistenceStatus(
                    GatewayPersistenceState.Ready,
                    GatewayPersistenceLane.TaskScheduler,
                    "Recovery is configured."),
                null,
                NativeStage: new NativeStageStatus(report, null)));
            using JsonDocument status = JsonDocument.Parse(statusOutput.ToString());
            JsonElement native = status.RootElement.GetProperty("nativeStage");
            Assert(native.GetProperty("state").GetString() == "current" &&
                native.GetProperty("inspected").GetBoolean() &&
                native.GetProperty("packages")[0].GetProperty("name").GetString() == "koffi" &&
                native.GetProperty("packages")[0].GetProperty("resolvesFrom").GetString() == "staged",
                "The native stage report did not survive NativeAOT.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static SessionLaunchResult RunAdmission(
        string directory,
        AdmissionRecordingLauncher launcher,
        string application,
        string? rootPath,
        SessionLaunchMode mode = SessionLaunchMode.Attached)
    {
        string requestPath = Path.Combine(directory, $"launch-{Guid.NewGuid():N}.json");
        File.WriteAllText(requestPath, SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
        {
            RequestId = "aot-native-stage",
            Mode = mode,
            Executable = @"C:\agent\node.exe",
            Arguments = ["openclaw.mjs"],
            WorkingDirectory = directory,
            NativeStage = new SessionNativeStageReference
            {
                ApplicationDirectory = application,
                RootPath = rootPath
            }
        }));
        _ = OpenClaw.SessionHost.Program.Run(
            ["--request", requestPath],
            launcher,
            TextWriter.Null,
            File.ReadAllText,
            OpenClaw.SessionHost.Program.WriteResult);
        return SessionLaunchProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));
    }

    private sealed class AdmissionRecordingLauncher : ISessionProcessLauncher
    {
        public List<SessionLaunchRequest> Requests { get; } = [];

        public int Run(SessionLaunchRequest request)
        {
            Requests.Add(request);
            return 0;
        }

        public SessionDetachedProcess Start(SessionLaunchRequest request, string helperPath) =>
            throw new InvalidOperationException("The admission scenario launches attached.");
    }
}
