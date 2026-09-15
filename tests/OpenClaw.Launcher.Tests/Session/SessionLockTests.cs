using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionLockTests
{
    private static NamedSessionLock CreateLock(string scope) =>
        new($"{scope}-{Guid.NewGuid():N}");

    [Fact]
    public void LockIsAcquiredWhenFree()
    {
        NamedSessionLock sessionLock = CreateLock("free");

        using ISessionLockHandle? handle = sessionLock.TryAcquire(TimeSpan.Zero);

        Assert.NotNull(handle);
    }

    [Fact]
    public void SecondAcquisitionFailsWhileTheFirstIsHeld()
    {
        NamedSessionLock sessionLock = CreateLock("contended");
        using ISessionLockHandle? first = sessionLock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(first);

        // A separate thread stands in for a separate process: a named mutex is
        // reentrant for its owning thread, so the same thread would succeed.
        ISessionLockHandle? second = null;
        var thread = new Thread(() => second = sessionLock.TryAcquire(TimeSpan.Zero));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.Null(second);
    }

    [Fact]
    public void LockIsAvailableAgainAfterRelease()
    {
        NamedSessionLock sessionLock = CreateLock("release");
        sessionLock.TryAcquire(TimeSpan.Zero)!.Dispose();

        ISessionLockHandle? second = null;
        var thread = new Thread(() => second = sessionLock.TryAcquire(TimeSpan.FromSeconds(5)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));

        Assert.NotNull(second);
        second!.Dispose();
    }

    [Fact]
    public void DifferentScopesDoNotContend()
    {
        NamedSessionLock first = CreateLock("scope-a");
        NamedSessionLock second = CreateLock("scope-b");

        using ISessionLockHandle? firstHandle = first.TryAcquire(TimeSpan.Zero);
        ISessionLockHandle? secondHandle = null;
        var thread = new Thread(() => secondHandle = second.TryAcquire(TimeSpan.Zero));
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));

        Assert.NotNull(firstHandle);
        Assert.NotNull(secondHandle);
        secondHandle!.Dispose();
    }

    [Fact]
    public void WaitingCallerAcquiresOnceTheHolderReleases()
    {
        NamedSessionLock sessionLock = CreateLock("handoff");
        ISessionLockHandle? holder = sessionLock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(holder);

        using var waiterStarted = new ManualResetEventSlim();
        ISessionLockHandle? waiterHandle = null;
        var waiter = new Thread(() =>
        {
            waiterStarted.Set();
            waiterHandle = sessionLock.TryAcquire(TimeSpan.FromSeconds(30));
        });
        waiter.Start();

        Assert.True(waiterStarted.Wait(TimeSpan.FromSeconds(10)));
        holder!.Dispose();

        Assert.True(waiter.Join(TimeSpan.FromSeconds(30)));
        Assert.NotNull(waiterHandle);
        waiterHandle!.Dispose();
    }

    [Fact]
    public void AbandonedLockIsRecoveredRatherThanDeadlocking()
    {
        NamedSessionLock sessionLock = CreateLock("abandoned");

        // Abandon the actual kernel mutex, not a transferable lease (which
        // deliberately keeps ownership on a surviving dedicated thread).
        using var kernelMutex = new Mutex(false, sessionLock.Name);
        var abandoner = new Thread(() => kernelMutex.WaitOne());
        abandoner.Start();
        Assert.True(abandoner.Join(TimeSpan.FromSeconds(10)));

        using ISessionLockHandle? handle = sessionLock.TryAcquire(TimeSpan.FromSeconds(10));

        Assert.NotNull(handle);
    }

    [Fact]
    public void AsyncContinuationCanReleaseLeaseFromAnotherThread()
    {
        NamedSessionLock sessionLock = CreateLock("cross-thread");
        ISessionLockHandle handle = sessionLock.TryAcquire(TimeSpan.Zero)!;
        var continuation = new Thread(handle.Dispose);
        continuation.Start();
        Assert.True(continuation.Join(TimeSpan.FromSeconds(10)));
        using ISessionLockHandle? next = sessionLock.TryAcquire(TimeSpan.Zero);
        Assert.NotNull(next);
    }

    [Fact]
    public void DisposingTwiceIsSafe()
    {
        NamedSessionLock sessionLock = CreateLock("double-dispose");
        ISessionLockHandle handle = sessionLock.TryAcquire(TimeSpan.Zero)!;

        handle.Dispose();
        handle.Dispose();

        using ISessionLockHandle? reacquired = sessionLock.TryAcquire(TimeSpan.FromSeconds(5));
        Assert.NotNull(reacquired);
    }

    [Fact]
    public void NameIsScopedToTheCurrentUserSession()
    {
        var sessionLock = new NamedSessionLock("PFN:OpenClaw.Gateway_abc123");

        Assert.StartsWith("Local\\", sessionLock.Name, StringComparison.Ordinal);
        Assert.DoesNotContain(':', sessionLock.Name[6..]);
        Assert.DoesNotContain('\\', sessionLock.Name[6..]);
    }

    [Fact]
    public void EmptyScopeIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new NamedSessionLock("  "));
    }

    [Fact]
    public void NegativeTimeoutIsRejected()
    {
        NamedSessionLock sessionLock = CreateLock("negative");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => sessionLock.TryAcquire(TimeSpan.FromSeconds(-1)));
    }
}
