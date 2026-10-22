using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

/// <summary>
/// Runs the real helper's state-transfer worker under the real invocation job, so cancellation
/// is proven against actual processes. The packaged OpenClaw is a fixture script and the state
/// gate is redirected to a temporary directory; nothing here touches an installed session.
/// </summary>
public sealed class StateTransferInvocationProcessTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task CancellingARunningStateTransferStopsTheWorkerAndItsDescendantsBeforeReturning()
    {
        string pipeName = "openclaw-state-cancel-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string requestPath = WriteRequest($$"""
            import { connect } from "node:net";
            const socket = connect("\\\\.\\pipe\\{{pipeName}}");
            socket.once("connect", () => socket.write(JSON.stringify({ pid: process.pid }) + "\n"));
            setInterval(() => {}, 1000);
            """);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using WindowsInvocationJob job = WindowsInvocationJob.Create(identity.User!.Value);
        using Process coordinator = StartHelper(job.StateTransferMode, requestPath);
        job.MarkDispatched();
        await pipe.WaitForConnectionAsync(budget.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using JsonDocument metadata = JsonDocument.Parse((await reader.ReadLineAsync(budget.Token))!);
        using Process descendant = Process.GetProcessById(metadata.RootElement.GetProperty("pid").GetInt32());
        try
        {
            Assert.False(descendant.HasExited);

            job.RequestStop();
            job.Stop();

            Assert.True(descendant.HasExited);
            await coordinator.WaitForExitAsync(budget.Token);
            Assert.False(File.Exists(SessionLaunchProtocol.ResultPathFor(requestPath)));
        }
        finally
        {
            StateTransferProcessFixture.Stop(descendant);
            StateTransferProcessFixture.Stop(coordinator);
        }
    }

    [Fact]
    public async Task AWorkerAdmittedAfterCancellationReportsItThroughTheStateTransferResult()
    {
        string marker = Path.Combine(_root, "unexpected-start.txt");
        string requestPath = WriteRequest($$"""
            import { writeFileSync } from "node:fs";
            writeFileSync({{JsonSerializer.Serialize(marker)}}, "started");
            """);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using WindowsInvocationJob job = WindowsInvocationJob.Create(identity.User!.Value);
        job.Stop();
        using Process worker = StartHelper(
            WindowsInvocationJob.StateTransferModePrefix + job.StateTransferMode[WindowsInvocationJob.StateTransferCoordinatorModePrefix.Length..],
            requestPath);
        await worker.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

        SessionStateTransferResult result = SessionStateTransferProtocol.ReadResult(
            File.ReadAllText(SessionLaunchProtocol.ResultPathFor(requestPath)),
            Path.GetFileNameWithoutExtension(requestPath)["state-transfer-".Length..]);
        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, worker.ExitCode);
        Assert.Contains("stopped before", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(marker));
    }

    private Process StartHelper(string mode, string requestPath)
    {
        Assert.True(File.Exists(HelperPath), $"The guest helper was not copied next to the tests: {HelperPath}");
        var start = new ProcessStartInfo
        {
            FileName = HelperPath,
            ArgumentList = { mode, requestPath },
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["OPENCLAW_SESSION_STATE_DIRECTORY"] = Path.Combine(_root, "state-transfer");
        return Process.Start(start)!;
    }

    private string WriteRequest(string openclawScript)
    {
        string workspace = Path.Combine(_root, "workspace");
        string application = Path.Combine(_root, "app");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(application);
        File.WriteAllText(Path.Combine(application, "package.json"), """{"type":"module"}""");
        File.WriteAllText(Path.Combine(application, "openclaw.mjs"), openclawScript);
        string node = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("The fixture needs node.exe on PATH.");
        string requestId = Guid.NewGuid().ToString("N");
        string requestPath = Path.Combine(workspace, $"state-transfer-{requestId}.json");
        // A dry-run capture reaches the fixture application without writing an archive.
        File.WriteAllText(requestPath, SessionStateTransferProtocol.SerializeRequest(new SessionStateTransferRequest
        {
            RequestId = requestId,
            Action = SessionStateTransferAction.Capture,
            DryRun = true,
            WorkspaceDirectory = workspace,
            NodePath = node,
            ApplicationDirectory = application
        }));
        return requestPath;
    }
}
