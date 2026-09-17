namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// A clock whose timers fire at once and whose time jumps forward by whatever
/// delay was asked for.
/// </summary>
/// <remarks>
/// Lets a wait loop run its whole budget in a test without waiting for real
/// time: the loop still observes a deadline passing, but no thread sleeps. A
/// test that slept for the real budget would be both slow and, on a loaded
/// machine, flaky.
/// </remarks>
internal sealed class AdvancingTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    /// <summary>How many timers were created, i.e. how many waits occurred.</summary>
    public int WaitCount { get; private set; }

    public override DateTimeOffset GetUtcNow() =>
        new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        WaitCount++;
        if (dueTime > TimeSpan.Zero)
        {
            Interlocked.Add(ref _ticks, dueTime.Ticks);
        }

        return new ImmediateTimer(callback, state);
    }

    // Fired off the thread pool rather than inline, so the caller finishes
    // constructing its own state before the callback runs.
    private sealed class ImmediateTimer : ITimer
    {
        public ImmediateTimer(TimerCallback callback, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => callback(state));

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
