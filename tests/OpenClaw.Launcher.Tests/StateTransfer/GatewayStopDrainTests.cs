using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
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
    private readonly string _helper = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task StoppingASupervisedGatewayReturnsOnlyAfterItsWholeTreeHasExited()
    {
        using SupervisedGateway gateway = await StartAsync("");

        Assert.False(gateway.Descendant.HasExited);

        SessionInspectResult result = SessionTerminator.Stop(gateway.StopRequest("stop"), File.ReadAllText);

        Assert.Null(result.Error);
        Assert.True(gateway.Supervisor.HasExited);
        Assert.True(gateway.Descendant.HasExited);
        AssertExited(gateway.Primary);
        AssertExited(gateway.Descendant);
    }

    [Fact]
    public async Task TheStopHelperLetsTheGatewayShutDownBeforeItsTreeIsTerminated()
    {
        // A gateway terminated outright never releases its agent database lease, so the next
        // start pays crash recovery. The marker stands in for that release.
        string marker = Path.Combine(_root, "released.txt");
        using SupervisedGateway gateway = await StartAsync($$"""
            process.on("SIGINT", () => {
              writeFileSync({{JsonSerializer.Serialize(marker)}}, "released");
              process.exit(0);
            });
            """);
        string requestPath = Path.Combine(_root, "stop.request.json");
        File.WriteAllText(requestPath, SessionInspectProtocol.SerializeRequest(gateway.StopRequest("graceful-stop")));

        using Process stop = Process.Start(new ProcessStartInfo
        {
            FileName = _helper,
            ArgumentList = { "--stop", requestPath },
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        await stop.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);
        SessionInspectResult result = SessionInspectProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)));

        Assert.Equal(0, stop.ExitCode);
        Assert.Null(result.Error);
        Assert.Equal("released", File.ReadAllText(marker));
        AssertExited(gateway.Supervisor);
        AssertExited(gateway.Primary);
        AssertExited(gateway.Descendant);
    }

    [Fact]
    public async Task AGatewayThatDoesNotStopItselfIsStillTerminatedAfterTheGracePeriod()
    {
        using SupervisedGateway gateway = await StartAsync("process.on(\"SIGINT\", () => {});");
        int interrupted = 0;

        SessionInspectResult result = SessionTerminator.Stop(
            gateway.StopRequest("forced-stop"),
            File.ReadAllText,
            grace: new GatewayStopGrace(
                members =>
                {
                    interrupted = members.Count;
                    return true;
                },
                TimeSpan.FromMilliseconds(200)));

        Assert.Null(result.Error);
        Assert.True(interrupted >= 2);
        AssertExited(gateway.Supervisor);
        AssertExited(gateway.Primary);
        AssertExited(gateway.Descendant);
    }

    /// <summary>Starts the real supervisor on a node gateway that runs <paramref name="setup"/> before reporting ready.</summary>
    private async Task<SupervisedGateway> StartAsync(string setup)
    {
        string pipeName = "openclaw-gateway-stop-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string script = Path.Combine(_root, "gateway.mjs");
        File.WriteAllText(script, $$"""
            import { spawn } from "node:child_process";
            import { writeFileSync } from "node:fs";
            {{setup}}
            const child = `
              import { connect } from "node:net";
              const socket = connect("\\\\\\\\.\\\\pipe\\\\{{pipeName}}");
              socket.once("connect", () => socket.write(JSON.stringify({ pid: process.pid, parentPid: process.ppid }) + "\\n"));
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
        var start = new ProcessStartInfo
        {
            FileName = _helper,
            ArgumentList = { "--supervise", requestPath },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_SESSION_STATE_DIRECTORY"] = Path.Combine(_root, "state-access");
        Process supervisor = Process.Start(start)!;
        try
        {
            await pipe.WaitForConnectionAsync(budget.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using JsonDocument metadata = JsonDocument.Parse((await reader.ReadLineAsync(budget.Token).ConfigureAwait(false))!);
            Process descendant = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
            Process primary = Process.GetProcessById(metadata.RootElement.GetProperty("parentPid").GetInt32());
            _ = primary.SafeHandle;
            _ = descendant.SafeHandle;
            return new SupervisedGateway(supervisor, primary, descendant, _helper, statusPath);
        }
        catch
        {
            StateTransferProcessFixture.Stop(supervisor);
            supervisor.Dispose();
            throw;
        }
    }

    private static void AssertExited(Process process)
    {
        using var wait = new EventWaitHandle(false, EventResetMode.AutoReset);
        wait.SafeWaitHandle = new SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false);
        Assert.True(wait.WaitOne(0), $"Process {process.Id} was not signaled when stop returned.");
    }

    private sealed record SupervisedGateway(
        Process Supervisor, Process Primary, Process Descendant, string HelperPath, string StatusPath) : IDisposable
    {
        public SessionInspectRequest StopRequest(string requestId) => new()
        {
            RequestId = requestId,
            ProcessId = Supervisor.Id,
            HelperPath = HelperPath,
            ProcessStartTimeUtc = Supervisor.StartTime.ToUniversalTime(),
            StatusPath = StatusPath
        };

        public void Dispose()
        {
            foreach (Process process in new[] { Descendant, Primary, Supervisor })
            {
                StateTransferProcessFixture.Stop(process);
                process.Dispose();
            }
        }
    }
}
