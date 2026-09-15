using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Records every lifecycle call so tests can assert ordering and ownership.
/// </summary>
internal sealed class FakeMxcSessionClient : IMxcSessionClient
{
    private int _provisionCount;

    public List<string> Calls { get; } = [];

    public List<string> ProvisionedAppIds { get; } = [];

    public string SandboxIdPrefix { get; set; } = "iso:sandbox";

    public MxcProvisionMetadata? Metadata { get; set; } = new(
        "agent_1",
        "S-1-5-21-0-0-0-1001",
        @"C:\Users\agent_1\Shared");

    public Exception? ProvisionFailure { get; set; }

    public Exception? StartFailure { get; set; }

    public Func<MxcSandboxId, Exception?>? StartFailureForSandbox { get; set; }

    public Exception? StopFailure { get; set; }

    public Exception? DeprovisionFailure { get; set; }

    public Exception? ExecuteFailure { get; set; }

    public MxcExecutionResult ExecutionResult { get; set; } =
        new(0, string.Empty, string.Empty);

    public List<string> ExecutedCommandLines { get; } = [];

    public Func<MxcExecutionRequest, Task<MxcExecutionResult>>? ExecuteBehavior { get; set; }

    public Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request,
        CancellationToken cancellationToken)
    {
        Calls.Add("provision");
        ProvisionedAppIds.Add(request.AppId);
        if (ProvisionFailure is not null)
        {
            return Task.FromException<MxcProvisionResult>(ProvisionFailure);
        }

        _provisionCount++;
        return Task.FromResult(new MxcProvisionResult(
            MxcSandboxId.Parse($"{SandboxIdPrefix}{_provisionCount}"),
            Metadata,
            null));
    }

    public Task StartAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken)
    {
        Calls.Add($"start:{sandboxId.Value}");
        Exception? failure = StartFailureForSandbox?.Invoke(sandboxId) ?? StartFailure;
        return failure is not null
            ? Task.FromException(failure)
            : Task.CompletedTask;
    }

    public Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken)
    {
        Calls.Add($"execute:{sandboxId.Value}");
        ExecutedCommandLines.Add(request.CommandLine);
        return ExecuteFailure is not null
            ? Task.FromException<MxcExecutionResult>(ExecuteFailure)
            : ExecuteBehavior is not null ? ExecuteBehavior(request) : Task.FromResult(ExecutionResult);
    }

    public int AttachedExitCode { get; set; }

    public List<string> AttachedCommandLines { get; } = [];

    public Func<CancellationToken, Task<int>>? AttachedBehavior { get; set; }

    public Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken)
    {
        Calls.Add($"execute-attached:{sandboxId.Value}");
        AttachedCommandLines.Add(request.CommandLine);
        return AttachedBehavior is not null
            ? AttachedBehavior(cancellationToken)
            : ExecuteFailure is not null
                ? Task.FromException<int>(ExecuteFailure)
                : Task.FromResult(AttachedExitCode);
    }

    public Task StopAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken)
    {
        Calls.Add($"stop:{sandboxId.Value}");
        return StopFailure is not null
            ? Task.FromException(StopFailure)
            : Task.CompletedTask;
    }

    public Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken)
    {
        Calls.Add($"deprovision:{sandboxId.Value}");
        return DeprovisionFailure is not null
            ? Task.FromException(DeprovisionFailure)
            : Task.CompletedTask;
    }
}
