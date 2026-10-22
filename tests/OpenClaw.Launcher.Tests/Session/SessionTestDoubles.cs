using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

internal sealed class AlwaysFreeLock : ISessionLock
{
    public int HeldCount { get; private set; }

    public int TotalAcquisitions { get; private set; }

    public ISessionLockHandle? TryAcquire(TimeSpan timeout)
    {
        HeldCount++;
        TotalAcquisitions++;
        return new Handle(this);
    }

    private sealed class Handle(AlwaysFreeLock owner) : ISessionLockHandle
    {
        public void Dispose() => owner.HeldCount--;
    }
}

internal sealed class NeverFreeLock : ISessionLock
{
    public ISessionLockHandle? TryAcquire(TimeSpan timeout) => null;
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// A monotonic clock that moves only when a test advances it.
/// </summary>
/// <remarks>
/// The wall clock is left to the system, so a duration measured from it
/// rather than from the monotonic timestamp would miss the advanced time.
/// </remarks>
internal sealed class ManualMonotonicTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
}
