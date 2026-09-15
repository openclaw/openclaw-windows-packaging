using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

internal sealed class AlwaysFreeLock : ISessionLock
{
    public int HeldCount { get; private set; }

    public ISessionLockHandle? TryAcquire(TimeSpan timeout)
    {
        HeldCount++;
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
