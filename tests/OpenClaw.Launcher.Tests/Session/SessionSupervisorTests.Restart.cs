using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed partial class SessionSupervisorTests
{
    private const string Application = """
        import { existsSync, readFileSync, writeFileSync, unlinkSync } from "node:fs";
        import { join } from "node:path";
        import net from "node:net";
        import { spawn } from "node:child_process";
        import { randomUUID } from "node:crypto";
        const root = process.env.RESTART_TEST_ROOT;
        const scenario = process.env.RESTART_TEST_SCENARIO;
        const counter = join(root, "generations.txt");
        const handoffPath = join(root, "handoff.json");
        const args = process.argv.slice(2);
        if (args[0] !== "gateway") throw new Error("Missing gateway command");
        if (args[1] === "run") {
          const generation = existsSync(counter) ? Number(readFileSync(counter, "utf8")) + 1 : 1;
          writeFileSync(counter, String(generation));
          writeFileSync(join(root, `generation-${generation}.json`), JSON.stringify({
            pid: process.pid, instance: randomUUID(), cwd: process.cwd(), args,
            mode: process.env.OPENCLAW_SUPERVISOR_MODE,
          }));
          console.log(`generation ${generation}`);
          if (generation === 1 && scenario === "descendant") {
            const child = spawn(process.execPath, ["--input-type=module", "--eval", `
              import net from "node:net";
              const pipe = net.connect(process.env.RESTART_TEST_PIPE, () => pipe.write(process.pid + "\\n"));
              pipe.once("data", () => process.send("ready"));
            `], { stdio: ["ignore", "ignore", "ignore", "ipc"] });
            child.once("message", () => { child.disconnect(); child.unref(); });
          }
          if (generation === 1 && !scenario.startsWith("missing")) {
            writeFileSync(handoffPath, JSON.stringify({
              kind: "gateway-supervisor-restart-handoff", version: 1, intentId: "fixture-intent",
              pid: scenario === "pid-mismatch" ? process.pid + 1 : process.pid,
              supervisorMode: scenario === "other-supervisor" ? "systemd" : "external",
              restartKind: scenario === "update" ? "update-process" : "full-process",
            }));
          }
          process.exitCode = generation > 1 ? 7 : scenario === "accepted-failure" || scenario === "missing-failure" ? 1 : 0;
        } else {
          if (JSON.stringify(args.slice(1, 4)) !== JSON.stringify(["restart-handoff", "consume", "--expected-pid"]) ||
              args.length !== 6 || args[5] !== "--json") throw new Error("Incorrect consume arguments");
          const emit = () => {
            if (scenario === "command-failure") { process.exitCode = 23; return; }
            if (scenario === "malformed") { process.stdout.write("not json"); return; }
            let result = { ok: true, protocol: "openclaw.gateway.restart-handoff", protocolVersion: 1,
              status: "none", reason: "missing" };
            if (existsSync(handoffPath)) {
              const handoff = JSON.parse(readFileSync(handoffPath, "utf8"));
              result = handoff.pid === Number(args[4])
                ? { ...result, status: "accepted", handoff }
                : { ...result, status: "rejected", reason: "pid-mismatch" };
              if (result.status === "accepted") unlinkSync(handoffPath);
            }
            if (scenario === "wrong-protocol") result.protocol = "unrelated.contract";
            if (scenario === "newer-protocol") result.protocolVersion = 2;
            if (scenario === "not-ok") result.ok = false;
            if (scenario === "expired") result = { ...result, status: "rejected", reason: "expired" };
            process.stdout.write(JSON.stringify(result));
          };
          if (process.env.RESTART_TEST_PIPE && scenario !== "descendant") {
            const pipe = net.connect(process.env.RESTART_TEST_PIPE, () => pipe.write(`${process.pid}\n`));
            pipe.once("data", () => { pipe.end(); emit(); });
          } else {
            emit();
          }
        }
        """;

    private SessionLaunchRequest GatewayRequest(string scenario)
    {
        string node = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
            .First(File.Exists);
        string application = Path.Combine(_root, "openclaw.mjs");
        File.WriteAllText(application, Application);
        return new SessionLaunchRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            Mode = SessionLaunchMode.Detached,
            Executable = node,
            Arguments = [application, "gateway", "run"],
            RestartHandoffArguments = [application, "gateway", "restart-handoff", "consume"],
            WorkingDirectory = _root,
            Environment = new Dictionary<string, string>
            {
                ["OPENCLAW_SUPERVISOR_MODE"] = "external",
                ["RESTART_TEST_ROOT"] = _root,
                ["RESTART_TEST_SCENARIO"] = scenario
            },
            LogPath = Path.Combine(_root, "gateway.log"),
            StatusPath = Path.Combine(_root, "gateway.status.json")
        };
    }

    private int Run(SessionLaunchRequest request, Action<SessionSupervisorStatus>? observe = null)
    {
        string path = SessionSupervisor.RequestPathFor(request.StatusPath!);
        File.WriteAllText(path, SessionLaunchProtocol.SerializeRequest(request));
        return SessionSupervisor.Run(path, File.ReadAllText, (statusPath, json) =>
        {
            File.WriteAllText(statusPath, json);
            observe?.Invoke(SessionInspectProtocol.ReadStatus(json));
        }, new SessionStateAccess(Path.Combine(_root, "state")).EnterReader);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("accepted-failure")]
    public void MatchingHandoffStartsASuccessorAndItsFailureIsNotRetried(string scenario)
    {
        var statuses = new List<SessionSupervisorStatus>();
        int exitCode = Run(GatewayRequest(scenario), statuses.Add);

        Assert.Equal(7, exitCode);
        Assert.Equal("2", File.ReadAllText(Path.Combine(_root, "generations.txt")));
        using JsonDocument first = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "generation-1.json")));
        using JsonDocument second = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "generation-2.json")));
        Assert.NotEqual(first.RootElement.GetProperty("instance").GetString(), second.RootElement.GetProperty("instance").GetString());
        Assert.Equal(_root, second.RootElement.GetProperty("cwd").GetString());
        Assert.Equal("external", second.RootElement.GetProperty("mode").GetString());
        Assert.Equal(["gateway", "run"], second.RootElement.GetProperty("args").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(2, statuses.Count(status => status.State == SessionSupervisorStatus.RunningState));
        Assert.Single(statuses.Select(status => (status.RequestId, status.ProcessId, status.ProcessStartTimeUtc)).Distinct());
        Assert.Equal(SessionSupervisorStatus.ExitedState, statuses[^1].State);
        string log = File.ReadAllText(Path.Combine(_root, "gateway.log"));
        Assert.Contains("generation 1", log, StringComparison.Ordinal);
        Assert.Contains("generation 2", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", 0)]
    [InlineData("missing-failure", 1)]
    [InlineData("pid-mismatch", 0)]
    [InlineData("expired", 0)]
    [InlineData("update", 0)]
    [InlineData("other-supervisor", 0)]
    public void AnExitWithoutAnAuthorizedFullProcessHandoffDoesNotRestart(string scenario, int expectedExit)
    {
        Assert.Equal(expectedExit, Run(GatewayRequest(scenario)));
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "generations.txt")));
    }

    [Theory]
    [InlineData("command-failure")]
    [InlineData("malformed")]
    [InlineData("wrong-protocol")]
    [InlineData("newer-protocol")]
    [InlineData("not-ok")]
    public void AnUnusableHandoffCommandFailsExplicitlyWithoutLaunchingAnotherGateway(string scenario)
    {
        SessionLaunchRequest request = GatewayRequest(scenario);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, Run(request));
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "generations.txt")));
        SessionSupervisorStatus status = SessionInspectProtocol.ReadStatus(File.ReadAllText(request.StatusPath!));
        Assert.Equal(SessionSupervisorStatus.ExitedState, status.State);
        Assert.Contains("restart handoff", status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStateLeaseStaysHeldBetweenGatewayGenerations()
    {
        var access = new SessionStateAccess(Path.Combine(_root, "state"));
        int running = 0;
        Run(GatewayRequest("accepted"), status =>
        {
            if (status.State == SessionSupervisorStatus.RunningState)
            {
                running++;
                Assert.Throws<SessionLaunchException>(() => access.EnterMaintenance());
            }
        });
        Assert.Equal(2, running);
        using IDisposable maintenance = access.EnterMaintenance();
    }

    [Fact]
    public async Task TheOldGenerationTreeExitsBeforeASuccessorStarts()
    {
        string pipeName = "openclaw-generation-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        SessionLaunchRequest request = GatewayRequest("descendant");
        request = request with
        {
            Environment = new Dictionary<string, string>(request.Environment!)
            {
                ["RESTART_TEST_PIPE"] = @"\\.\pipe\" + pipeName
            }
        };
        Process? descendant = null;
        int running = 0;
        Task<int> run = Task.Run(() => Run(request, status =>
        {
            if (status.State == SessionSupervisorStatus.RunningState && ++running == 2)
            {
                Assert.NotNull(descendant);
                Assert.True(descendant.HasExited, "The successor started before the old descendant exited.");
            }
        }));
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            descendant = Process.GetProcessById(int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture));
            _ = descendant.SafeHandle;
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync("release").ConfigureAwait(true);
            Assert.Equal(7, await run.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true));
            Assert.Equal(2, running);
        }
        finally
        {
            pipe.Dispose();
            if (descendant is not null)
            {
                if (!descendant.HasExited)
                {
                    descendant.Kill();
                }
                descendant.Dispose();
            }
        }
    }

    [Fact]
    public async Task StopDuringHandoffKillsTheConsumerAndCannotLaunchASuccessor()
    {
        string pipeName = "openclaw-restart-test-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        SessionLaunchRequest request = GatewayRequest("accepted");
        var environment = new Dictionary<string, string>(request.Environment!)
        {
            ["RESTART_TEST_PIPE"] = @"\\.\pipe\" + pipeName
        };
        request = request with { Environment = environment };
        string requestPath = SessionSupervisor.RequestPathFor(request.StatusPath!);
        File.WriteAllText(requestPath, SessionLaunchProtocol.SerializeRequest(request));
        string helperPath = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe");
        ProcessStartInfo start = new(helperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--supervise", requestPath }
        };
        start.Environment[SessionStateAccess.DirectoryVariable] = Path.Combine(_root, "state");
        using Process supervisor = Process.Start(start)!;
        try
        {
            await pipe.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
            using Process consumer = Process.GetProcessById(int.Parse(line!, System.Globalization.CultureInfo.InvariantCulture));
            _ = consumer.SafeHandle;
            SessionInspectResult stopped = SessionTerminator.Stop(new SessionInspectRequest
            {
                RequestId = "stop",
                ProcessId = supervisor.Id,
                ProcessStartTimeUtc = supervisor.StartTime.ToUniversalTime(),
                HelperPath = helperPath,
                StatusPath = request.StatusPath
            }, File.ReadAllText);
            Assert.Null(stopped.Error);
            Assert.False(stopped.ProcessFound && stopped.StartTimeMatches);
            Assert.True(consumer.WaitForExit(TimeSpan.FromSeconds(10)));
            Assert.Equal("1", File.ReadAllText(Path.Combine(_root, "generations.txt")));
        }
        finally
        {
            if (!supervisor.HasExited)
            {
                supervisor.Kill(entireProcessTree: true);
            }
            await supervisor.WaitForExitAsync().ConfigureAwait(true);
        }
    }
}
