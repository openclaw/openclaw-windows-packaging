namespace OpenClaw.Launcher.Session;

/// <summary>
/// A held lifecycle lock. Disposing releases it.
/// </summary>
public interface ISessionLockHandle : IDisposable
{
}

/// <summary>
/// Serializes session lifecycle transitions across processes.
/// </summary>
/// <remarks>
/// This guards provision, start, stop, and deprovision only. It is deliberately
/// not held for the lifetime of a foreground OpenClaw invocation: a long
/// interactive run would otherwise block every other command indefinitely, and
/// a crash would leave the next caller waiting on a lock whose owner is gone.
/// </remarks>
public interface ISessionLock
{
    /// <summary>
    /// Acquires the lock, or returns null if <paramref name="timeout"/> elapses.
    /// </summary>
    ISessionLockHandle? TryAcquire(TimeSpan timeout);
}

/// <summary>
/// Named-mutex lifecycle lock, scoped to one user and package identity.
/// </summary>
public sealed class NamedSessionLock : ISessionLock
{
    private readonly string _name;

    public NamedSessionLock(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        // Local\ keeps the lock inside the session of the current user, which
        // matches the one-session-per-user-and-package rule and avoids needing
        // rights on a Global object.
        _name = "Local\\OpenClawSessionLifecycle_" + Sanitize(scope);
    }

    public string Name => _name;

    public ISessionLockHandle? TryAcquire(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var mutex = new Mutex(initiallyOwned: false, _name);
        try
        {
            bool acquired;
            try
            {
                acquired = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died mid-transition. We now hold the
                // mutex; the caller is responsible for reconciling whatever
                // partial state that owner left behind.
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }

            var handle = new Handle(mutex);
            mutex = null!;
            return handle;
        }
        catch
        {
            mutex?.Dispose();
            throw;
        }
    }

    private static string Sanitize(string scope)
    {
        Span<char> buffer = scope.Length <= 128
            ? stackalloc char[scope.Length]
            : new char[scope.Length];

        for (int index = 0; index < scope.Length; index++)
        {
            char value = scope[index];
            buffer[index] = char.IsAsciiLetterOrDigit(value) ? value : '_';
        }

        return new string(buffer);
    }

    private sealed class Handle : ISessionLockHandle
    {
        private Mutex? _mutex;

        public Handle(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            Mutex? mutex = Interlocked.Exchange(ref _mutex, null);
            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            mutex.Dispose();
        }
    }
}
