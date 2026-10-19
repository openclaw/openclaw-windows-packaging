using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using OpenClaw.Launcher.Mxc;
using Sdk = Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Launcher;

internal sealed class WindowsMxcHostStdio(
    WindowsHostConsole? console = null,
    TimeProvider? clock = null,
    Func<Stream>? openInput = null) : IMxcHostStdio
{
    internal static WindowsMxcHostStdio Instance { get; } = new();

    private readonly WindowsHostConsole _console = console ?? WindowsHostConsole.Instance;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<Stream> _openInput = openInput ?? Console.OpenStandardInput;

    public bool IsTerminal => _console.HasTerminalStreams;

    public Sdk.MxcPtySize TerminalSize => _console.ReadTerminalSize();

    public void PrepareTerminal() => _console.PrepareTerminal();

    public Stream OpenOutput() => Console.OpenStandardOutput();

    public Stream OpenError() => Console.OpenStandardError();

    public async Task WatchSizeAsync(Action<Sdk.MxcPtySize> resize, CancellationToken cancellationToken)
    {
        Sdk.MxcPtySize previous = TerminalSize;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), _clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Sdk.MxcPtySize size = TerminalSize;
                if (size != previous)
                {
                    resize(size);
                    previous = size;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task CopyInputAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var ready = new TaskCompletionSource<SafeFileHandle>(TaskCreationOptions.RunContinuationsAsynchronously);
        using Stream source = _openInput();
        Task copy = Task.Factory.StartNew(() =>
        {
            SafeFileHandle thread = DuplicateCurrentThread();
            ready.SetResult(thread);
            byte[] buffer = new byte[81920];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read == 0 || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    destination.Write(buffer, 0, read);
                    destination.Flush();
                }
            }
            finally
            {
                destination.Dispose();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        await Task.WhenAny(copy, ready.Task).ConfigureAwait(false);
        if (!ready.Task.IsCompletedSuccessfully)
        {
            await copy.ConfigureAwait(false);
            return;
        }

        using SafeFileHandle reader = await ready.Task.ConfigureAwait(false);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration =
            cancellationToken.Register(() => stopped.TrySetResult());
        await Task.WhenAny(copy, stopped.Task).ConfigureAwait(false);
        try
        {
            // Console and inherited pipe reads are synchronous. Cancel the
            // dedicated reader thread, never the shared standard handle.
            // Repeat until joined: a one-shot cancel can race the next Read.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10), _clock);
            while (!copy.IsCompleted)
            {
                try
                {
                    if (!Native.CancelSynchronousIo(reader) && Marshal.GetLastPInvokeError() != 1168)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Console input cancellation failed.");
                    }
                }
                catch (ObjectDisposedException) when (copy.IsCompleted)
                {
                }
                await Task.WhenAny(copy, timer.WaitForNextTickAsync(CancellationToken.None).AsTask()).ConfigureAwait(false);
            }
            await copy.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception) when (
            cancellationToken.IsCancellationRequested &&
            (exception.HResult & 0xffff) is 995 or 109 or 232 or 233)
        {
        }
        catch (Sdk.MxcException exception) when (
            cancellationToken.IsCancellationRequested && exception.Code == Sdk.ErrorCode.BackendError)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static SafeFileHandle DuplicateCurrentThread()
    {
        nint process = Native.GetCurrentProcess();
        if (!Native.DuplicateHandle(
            process, Native.GetCurrentThread(), process, out SafeFileHandle thread, 0, false, 2))
        {
            int error = Marshal.GetLastPInvokeError();
            thread.Dispose();
            throw new Win32Exception(error, "The console reader thread could not be owned.");
        }
        return thread;
    }

    private static class Native
    {
        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll")]
        internal static extern nint GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            nint sourceProcess, nint sourceHandle, nint targetProcess,
            out SafeFileHandle targetHandle, uint access,
            [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelSynchronousIo(SafeFileHandle thread);
    }
}
