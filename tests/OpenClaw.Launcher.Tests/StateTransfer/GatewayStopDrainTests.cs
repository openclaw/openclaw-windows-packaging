using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

/// <summary>
/// Stopping a supervised gateway must not report success while any member of its tree is alive,
/// because restore captures state the moment the stop returns. Real helper, real node processes;
/// the state gate is redirected to a temporary directory.
/// </summary>
public sealed class GatewayStopDrainTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task StoppingASupervisedGatewayReturnsOnlyAfterItsWholeTreeHasExited()
    {
        string pipeName = "openclaw-gateway-stop-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string script = Path.Combine(_root, "gateway.mjs");
        File.WriteAllText(script, $$"""
            import { spawn } from "node:child_process";
            const child = `
              import { connect } from "node:net";
              const socket = connect("\\\\\\\\.\\\\pipe\\\\{{pipeName}}");
              socket.once("connect", () => socket.write(JSON.stringify({ pid: process.pid }) + "\\n"));
              setInterval(() => {}, 1000);
            `;
            // The descendant shares none of the supervisor's streams.
            spawn(process.execPath, ["--input-type=module", "-e", child], { stdio: "ignore" });
            setInterval(() => {}, 1000);
            """);
        string node = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("The fixture needs node.exe on PATH.");
        string statusPath = Path.Combine(_root, "gateway.status.json");
        string requestPath = SessionSupervisor.RequestPathFor(statusPath);
        File.WriteAllText(requestPath, SessionLaunchProtocol.SerializeRequest(new SessionLaunchRequest
        {
            RequestId = "gateway-stop",
            Mode = SessionLaunchMode.Detached,
            Executable = node,
            Arguments = [script],
            WorkingDirectory = _root,
            LogPath = Path.Combine(_root, "gateway.log"),
            StatusPath = statusPath,
            Environment = new Dictionary<string, string> { ["NODE_OPTIONS"] = string.Empty }
        }));
        string helper = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe");
        var start = new ProcessStartInfo
        {
            FileName = helper,
            ArgumentList = { "--supervise", requestPath },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_SESSION_STATE_DIRECTORY"] = Path.Combine(_root, "state-access");
        using Process supervisor = Process.Start(start)!;
        await pipe.WaitForConnectionAsync(budget.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using JsonDocument metadata = JsonDocument.Parse((await reader.ReadLineAsync(budget.Token))!);
        using Process descendant = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
        try
        {
            Assert.False(descendant.HasExited);

            SessionInspectResult result = SessionTerminator.Stop(
                new SessionInspectRequest
                {
                    RequestId = "stop",
                    ProcessId = supervisor.Id,
                    HelperPath = helper,
                    ProcessStartTimeUtc = supervisor.StartTime.ToUniversalTime(),
                    StatusPath = statusPath
                },
                File.ReadAllText);

            Assert.Null(result.Error);
            Assert.True(supervisor.HasExited);
            Assert.True(descendant.HasExited);
        }
        finally
        {
            StateTransferProcessFixture.Stop(descendant);
            StateTransferProcessFixture.Stop(supervisor);
        }
    }
}
