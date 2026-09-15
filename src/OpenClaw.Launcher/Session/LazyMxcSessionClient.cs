using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Session;

/// <summary>
/// Defers backend construction until a lifecycle call actually needs it.
/// </summary>
/// <remarks>
/// <c>clawctl status</c> can report local ownership without constructing
/// the backend, so it must work on a machine where the MXC runtime is missing.
/// Constructing the real client eagerly would turn a read-only status query
/// into a runtime-availability failure and hide the recorded state the user
/// asked about.
/// </remarks>
internal sealed class LazyMxcSessionClient(Func<IMxcSessionClient> create)
    : IMxcSessionClient
{
    private readonly Lazy<IMxcSessionClient> _inner = new(create);

    public Task<MxcProvisionResult> ProvisionAsync(
        MxcProvisionRequest request,
        CancellationToken cancellationToken) =>
        _inner.Value.ProvisionAsync(request, cancellationToken);

    public Task StartAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        _inner.Value.StartAsync(sandboxId, correlationVector, cancellationToken);

    public Task<MxcExecutionResult> ExecuteAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        _inner.Value.ExecuteAsync(
            sandboxId,
            request,
            correlationVector,
            cancellationToken);

    public Task<int> ExecuteAttachedAsync(
        MxcSandboxId sandboxId,
        MxcExecutionRequest request,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        _inner.Value.ExecuteAttachedAsync(
            sandboxId,
            request,
            correlationVector,
            cancellationToken);

    public Task StopAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        _inner.Value.StopAsync(sandboxId, correlationVector, cancellationToken);

    public Task DeprovisionAsync(
        MxcSandboxId sandboxId,
        string? correlationVector,
        CancellationToken cancellationToken) =>
        _inner.Value.DeprovisionAsync(
            sandboxId,
            correlationVector,
            cancellationToken);
}
