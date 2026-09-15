using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// A Task Scheduler that records what it was asked to do. Tests must never
/// register, run, or delete a real scheduled task on the developer's machine.
/// </summary>
internal sealed class FakeGatewayTaskScheduler : IGatewayTaskScheduler
{
    public List<string> Calls { get; } = [];

    public GatewayTaskProbe Probe { get; set; } = GatewayTaskProbe.Missing;

    public GatewayTaskOperation RegisterResult { get; set; } =
        GatewayTaskOperation.Success;

    public GatewayTaskOperation DeleteResult { get; set; } =
        GatewayTaskOperation.Success;

    public GatewayTaskOperation RunResult { get; set; } =
        GatewayTaskOperation.Success;

    public string? RegisteredXml { get; private set; }

    public Task<GatewayTaskProbe> QueryAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        Calls.Add($"query:{taskName}");
        return Task.FromResult(Probe);
    }

    public Task<GatewayTaskOperation> RegisterAsync(
        string taskName,
        string taskXml,
        CancellationToken cancellationToken)
    {
        Calls.Add($"register:{taskName}");
        RegisteredXml = taskXml;
        return Task.FromResult(RegisterResult);
    }

    public Task<GatewayTaskOperation> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        Calls.Add($"delete:{taskName}");
        return Task.FromResult(DeleteResult);
    }

    public Task<GatewayTaskOperation> RunAsync(
        string taskName,
        CancellationToken cancellationToken)
    {
        Calls.Add($"run:{taskName}");
        return Task.FromResult(RunResult);
    }
}
