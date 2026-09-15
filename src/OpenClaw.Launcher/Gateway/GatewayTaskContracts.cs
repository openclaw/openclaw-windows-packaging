namespace OpenClaw.Launcher.Gateway;

/// <summary>
/// What a task probe found. Deliberately three-valued.
/// </summary>
internal enum GatewayTaskPresence
{
    /// <summary>Task Scheduler reported no such task.</summary>
    Missing,

    /// <summary>The task exists and its definition was read.</summary>
    Present,

    /// <summary>
    /// The task could not be read. This is not the same as missing: the query
    /// may have been refused, and re-registering on a refused read is an
    /// unbounded retry whose write is as likely to be refused as the read was.
    /// </summary>
    Unreadable,
}

internal sealed record GatewayTaskProbe(
    GatewayTaskPresence Presence,
    GatewayTaskSnapshot? Snapshot,
    string? Detail)
{
    public static GatewayTaskProbe Missing { get; } =
        new(GatewayTaskPresence.Missing, null, null);

    public static GatewayTaskProbe Present(GatewayTaskSnapshot snapshot) =>
        new(GatewayTaskPresence.Present, snapshot, null);

    public static GatewayTaskProbe Unreadable(string detail) =>
        new(GatewayTaskPresence.Unreadable, null, detail);
}

internal sealed record GatewayTaskOperation(bool Succeeded, string? Detail)
{
    public static GatewayTaskOperation Success { get; } = new(true, null);

    public static GatewayTaskOperation Failure(string detail) => new(false, detail);
}

/// <summary>
/// The Task Scheduler operations gateway persistence needs.
/// </summary>
/// <remarks>
/// Behind an interface so lifecycle behavior is testable without registering
/// anything on the developer's machine. Tests must never create, run, or delete
/// a real scheduled task.
/// </remarks>
internal interface IGatewayTaskScheduler
{
    Task<GatewayTaskProbe> QueryAsync(
        string taskName,
        CancellationToken cancellationToken);

    Task<GatewayTaskOperation> RegisterAsync(
        string taskName,
        string taskXml,
        CancellationToken cancellationToken);

    Task<GatewayTaskOperation> DeleteAsync(
        string taskName,
        CancellationToken cancellationToken);

    Task<GatewayTaskOperation> RunAsync(
        string taskName,
        CancellationToken cancellationToken);
}
