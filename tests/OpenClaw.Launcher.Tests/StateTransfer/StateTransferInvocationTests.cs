using System.Security.Principal;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.StateTransfer;

public sealed class StateTransferInvocationTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private SessionWorkspaceOperation? _operation;

    public void Dispose()
    {
        _operation?.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task StateTransferRunsUnderTheInvocationLifetimeAndKeepsItsFilesUntilTheTreeDrains()
    {
        var lifetime = new RecordingLifetime();
        var (executor, backend, record, operation) = Create(lifetime);
        string commandLine = string.Empty;
        backend.ExecuteBehavior = request =>
        {
            commandLine = request.CommandLine;
            Assert.NotNull(request.ProcessCreated);
            Assert.NotNull(request.ForegroundExited);
            Assert.NotNull(request.CancelInvocation);
            request.ProcessCreated!();
            RunWorker();
            request.ForegroundExited!();
            return Task.FromResult(new OpenClaw.Launcher.Mxc.MxcExecutionResult(0, string.Empty, string.Empty));
        };
        lifetime.BeforeStop = () => lifetime.ExchangeFilesAtStop = Directory.GetFiles(_operation!.WorkspacePath, "state-transfer-*.json").Length;

        _ = await executor.TransferStateAsync(
            record, @"C:\package\helper.exe", operation, Request(), CancellationToken.None);
        Assert.Contains(lifetime.StateTransferMode, commandLine, StringComparison.Ordinal);
        Assert.Equal(["dispatched", "foreground-exited", "stop"], lifetime.Events);
        Assert.Equal(2, lifetime.ExchangeFilesAtStop);
        Assert.Empty(Directory.GetFiles(_operation!.WorkspacePath, "state-transfer-*.json"));
    }

    [Fact]
    public async Task CancellingStateTransferStopsTheWorkerTreeBeforeItsFilesAreReleased()
    {
        var lifetime = new RecordingLifetime();
        var (executor, backend, record, operation) = Create(lifetime);
        using var cancellation = new CancellationTokenSource();
        backend.ExecuteBehavior = request =>
        {
            request.ProcessCreated!();
            // The worker has started and written its result; Ctrl+C arrives before the host reads it.
            RunWorker();
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        };
        lifetime.BeforeStop = () => lifetime.ExchangeFilesAtStop = Directory.GetFiles(_operation!.WorkspacePath, "state-transfer-*.json").Length;

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => executor.TransferStateAsync(
            record, @"C:\package\helper.exe", operation, Request(), cancellation.Token));

        Assert.Equal(["dispatched", "request-stop", "stop"], lifetime.Events);
        Assert.Equal(2, lifetime.ExchangeFilesAtStop);
        Assert.Empty(Directory.GetFiles(_operation!.WorkspacePath, "state-transfer-*.json"));
    }

    [Fact]
    public async Task ADrainFailureSurfacesInsteadOfReleasingTheExchange()
    {
        var lifetime = new RecordingLifetime { StopFailure = new TimeoutException("fixture drain timed out") };
        var (executor, backend, record, operation) = Create(lifetime);
        backend.ExecuteBehavior = request =>
        {
            request.ProcessCreated!();
            RunWorker();
            return Task.FromResult(new OpenClaw.Launcher.Mxc.MxcExecutionResult(0, string.Empty, string.Empty));
        };

        SessionException failure = await Assert.ThrowsAsync<SessionException>(() => executor.TransferStateAsync(
            record, @"C:\package\helper.exe", operation, Request(), CancellationToken.None));

        Assert.Contains("could not confirm process exit", failure.Message, StringComparison.Ordinal);
    }

    private void RunWorker()
    {
        string request = Directory.GetFiles(_operation!.WorkspacePath, "state-transfer-*.json")
            .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
        int exit = SessionStateTransfer.Run(
            request, File.ReadAllText, File.WriteAllText,
            _ => new SessionStateTransfer(
                Path.Combine(_root, "agent"), Path.Combine(_root, "transfer"), new SessionStateTransferTests.ArchiveApplication()));
        Assert.Equal(0, exit);
    }

    private SessionStateTransferRequest Request() => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Action = SessionStateTransferAction.Inspect,
        WorkspaceDirectory = _root,
        NodePath = @"C:\agent\node.exe",
        ApplicationDirectory = @"C:\package\app"
    };

    private (SessionExecutor Executor, Session.FakeMxcSessionClient Backend, SessionRecord Record, SessionWorkspaceOperation Operation) Create(
        RecordingLifetime lifetime)
    {
        Directory.CreateDirectory(Path.Combine(_root, "agent"));
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var record = new SessionRecord
        {
            SchemaVersion = 1,
            SandboxId = "iso:sandbox1",
            ApplicationId = "PFN:OpenClaw.Gateway_abc123",
            Generation = "fixture-generation",
            WorkspacePath = _root,
            AgentUserSid = identity.User?.Value
        };
        var backend = new Session.FakeMxcSessionClient();
        var operation = new SessionWorkspaceOperation(record, _ => true, privateData: true,
            log: message => throw new InvalidOperationException(message));
        var executor = new SessionExecutor(backend, _ => { }, createInvocationLifetime: _ => lifetime);
        _operation = operation;
        return (executor, backend, record, operation);
    }

    private sealed class RecordingLifetime : IInvocationLifetime
    {
        public List<string> Events { get; } = [];
        public Exception? StopFailure { get; init; }
        public Action? BeforeStop { get; set; }
        public int ExchangeFilesAtStop { get; set; }
        public string Mode => "--coordinate-invocation=fixture";
        public string StateTransferMode => "--coordinate-state-transfer=fixture";
        public void MarkDispatched() => Events.Add("dispatched");
        public void RequestStop() => Events.Add("request-stop");
        public void ForegroundExited() => Events.Add("foreground-exited");

        public void Stop()
        {
            BeforeStop?.Invoke();
            Events.Add("stop");
            if (StopFailure is not null)
            {
                throw StopFailure;
            }
        }

        public void Dispose()
        {
        }
    }
}
