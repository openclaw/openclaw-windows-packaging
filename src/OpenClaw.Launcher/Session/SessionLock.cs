namespace OpenClaw.Launcher.Session;

/// <summary>
/// A held lifecycle lock. Disposing releases it.
/// </summary>
internal interface ISessionLockHandle : IDisposable
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
internal interface ISessionLock
{
    /// <summary>
    /// Acquires the lock, or returns null if <paramref name="timeout"/> elapses.
    /// </summary>
    ISessionLockHandle? TryAcquire(TimeSpan timeout);
}

/// <summary>
/// Named-mutex lifecycle lock, scoped to one installation's LocalState path.
/// </summary>
internal sealed class NamedSessionLock : ISessionLock
{
    private readonly string _name;

    public NamedSessionLock(string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        // The LocalState path makes this installation-specific. Global\ then
        // carries that same lock across console and remote desktop sessions.
        _name = "Global\\OpenClawSessionLifecycle_" + Sanitize(scope);
    }

    public string Name => _name;

    public ISessionLockHandle? TryAcquire(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var handle = new Handle(_name, timeout);
        try
        {
            if (handle.Acquired)
            {
                return handle;
            }
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        handle.Dispose();
        return null;
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
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource<bool> _acquired = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _owner;
        private int _disposed;

        public Handle(string name, TimeSpan timeout)
        {
            // Mutex ownership is thread-affine; lifecycle methods await backend
            // I/O and can dispose on a different thread. Keep ownership on one
            // dedicated thread while the caller holds this transferable lease.
            _owner = new Thread(() => Hold(name, timeout)) { IsBackground = true };
            _owner.Start();
        }

        public bool Acquired => _acquired.Task.GetAwaiter().GetResult();

        private void Hold(string name, TimeSpan timeout)
        {
            try
            {
                using var mutex = new Mutex(false, name);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(timeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                _acquired.SetResult(acquired);
                if (acquired)
                {
                    _release.Wait();
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or
                WaitHandleCannotBeOpenedException)
            {
                _acquired.TrySetException(exception);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _release.Set();
            _owner.Join();
            _release.Dispose();
        }
    }
}
