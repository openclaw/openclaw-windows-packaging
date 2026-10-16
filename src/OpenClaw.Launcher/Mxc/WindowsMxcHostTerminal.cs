using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Mxc.Sdk.V1;

namespace OpenClaw.Launcher.Mxc;

internal interface IMxcHostTerminal
{
    bool IsInteractive { get; }
    IDisposable EnterPtyMode();
    MxcPtySize GetSize();
    Task PumpInputAsync(Stream destination, CancellationToken cancellationToken);
    Task PumpOutputAsync(Stream source, bool standardError, CancellationToken cancellationToken);
    Task WatchResizeAsync(Action<MxcPtySize> resize, CancellationToken cancellationToken);
}

internal interface IMxcConsoleInputApi
{
    uint CurrentThreadId { get; }
    nint OpenThreadForSynchronousCancel(uint threadId);
    uint WaitForInput(nint handle, uint milliseconds);
    bool TryReadCharacters(nint handle, byte[] buffer, out uint bytesRead);
    void CancelSynchronousRead(nint threadHandle);
    void CloseThreadHandle(nint threadHandle);
    int LastError { get; }
}

internal static class WindowsMxcConsoleInputReader
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const int ErrorOperationAborted = 995;
    private const uint PollMilliseconds = 50;

    public static Task PumpAsync(
        nint handle,
        System.Threading.Channels.ChannelWriter<byte[]> writer,
        IMxcConsoleInputApi api,
        CancellationToken cancellationToken) =>
        Task.Factory.StartNew(
            () => ReadLoop(handle, writer, api, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static void ReadLoop(
        nint handle,
        System.Threading.Channels.ChannelWriter<byte[]> writer,
        IMxcConsoleInputApi api,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = new byte[4096];
        using var readerFinished = new ManualResetEventSlim();
        uint readerThreadId = api.CurrentThreadId;
        nint cancelThread = api.OpenThreadForSynchronousCancel(readerThreadId);
        if (cancelThread == 0)
        {
            throw new Win32Exception(api.LastError);
        }

        Task? cancellationWorker = null;
        CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
            cancellationWorker = Task.Run(() =>
            {
                // CancelSynchronousIo can win the narrow interval before ReadFile
                // enters the kernel. Retry until this owned reader has joined so
                // it cannot start an uncancelled blocking read after the first attempt.
                while (!readerFinished.IsSet)
                {
                    api.CancelSynchronousRead(cancelThread);
                    readerFinished.Wait(1);
                }
            }));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                uint wait = api.WaitForInput(handle, PollMilliseconds);
                if (wait == WaitTimeout)
                {
                    continue;
                }
                if (wait != WaitObject0)
                {
                    throw new Win32Exception(api.LastError);
                }

                // ReadFile remains the Windows VT keyboard translator. Because a
                // console handle can be signaled by records it filters, run this
                // blocking call on an owned thread and cancel/join it on shutdown.
                cancellationToken.ThrowIfCancellationRequested();
                if (!api.TryReadCharacters(handle, buffer, out uint read))
                {
                    int error = api.LastError;
                    if (error == ErrorOperationAborted && cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    throw new Win32Exception(error);
                }
                if (read == 0)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (!writer.TryWrite(buffer.AsMemory(0, (int)read).ToArray()))
                {
                    return;
                }
            }
        }
        finally
        {
            readerFinished.Set();
            cancellationRegistration.Dispose();
            cancellationWorker?.GetAwaiter().GetResult();
            api.CloseThreadHandle(cancelThread);
        }
    }
}

internal sealed class WindowsMxcHostTerminal : IMxcHostTerminal
{
    private readonly bool _isConsoleInput;
    private readonly bool _isConsoleOutput;
    private int _inputModeReady;

    public WindowsMxcHostTerminal()
    {
        _isConsoleInput = IsConsoleHandle(StdInput);
        _isConsoleOutput = IsConsoleHandle(StdOutput);
    }

    internal static bool IsPtyAvailable(
        bool isConsoleInput,
        bool isConsoleOutput) =>
        isConsoleInput && isConsoleOutput;

    public bool IsInteractive => IsPtyAvailable(
        _isConsoleInput,
        _isConsoleOutput);

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned scope owns and restores the captured console state.")]
    public IDisposable EnterPtyMode()
    {
        nint input = Native.GetStdHandle(StdInput);
        if (!IsInteractive || input == 0 || input == InvalidHandleValue)
        {
            throw new InvalidOperationException("A PTY requires console input and output handles.");
        }

        IDisposable capture = WindowsHostConsole.Instance.Capture(_ => { });
        List<IDisposable> outputModes = [];
        try
        {
            WindowsHostConsole.Instance.InitializeUtf8();
            if (!Native.GetConsoleMode(input, out uint mode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            SetVirtualTerminalInput(input, mode);
            Interlocked.Exchange(ref _inputModeReady, 1);
            TryEnableOutputMode(Console.Out, outputModes);
            TryEnableOutputMode(Console.Error, outputModes);

            return new Scope(() =>
            {
                try
                {
                    for (int index = outputModes.Count - 1; index >= 0; index--)
                    {
                        outputModes[index].Dispose();
                    }
                    Native.SetConsoleMode(input, mode);
                }
                finally
                {
                    Interlocked.Exchange(ref _inputModeReady, 0);
                    capture.Dispose();
                }
            });
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    public MxcPtySize GetSize()
    {
        nint output = Native.GetStdHandle(StdOutput);
        if (output == 0 || output == InvalidHandleValue ||
            !Native.GetConsoleScreenBufferInfo(
                output,
                out ConsoleScreenBufferInfo info))
        {
            return MxcPtySize.Default;
        }

        int columns = Math.Clamp(info.Window.Right - info.Window.Left + 1, 1, short.MaxValue);
        int rows = Math.Clamp(info.Window.Bottom - info.Window.Top + 1, 1, short.MaxValue);
        return new MxcPtySize((ushort)rows, (ushort)columns);
    }

    public async Task PumpInputAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        using SafeFileHandle? input = DuplicateStandardHandle(StdInput);
        if (input is null)
        {
            return;
        }

        if (_isConsoleInput)
        {
            IDisposable? inputMode = Interlocked.CompareExchange(ref _inputModeReady, 1, 1) == 1
                ? null
                : EnterConsoleInputMode();
            try
            {
                await PumpConsoleInputAsync(
                    input.DangerousGetHandle(),
                    destination,
                    NativeConsoleInputApi.Instance,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                inputMode?.Dispose();
            }
        }
        else if (Native.GetFileType(input.DangerousGetHandle()) == FileTypePipe)
        {
            await PumpPipeInputAsync(input, destination, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await PumpDiskInputAsync(input, destination, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Console standard streams are process-owned and must remain open.")]
    public async Task PumpOutputAsync(
        Stream source,
        bool standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        Stream output = standardError
            ? Console.OpenStandardError()
            : Console.OpenStandardOutput();
        await source.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WatchResizeAsync(
        Action<MxcPtySize> resize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resize);
        MxcPtySize previous = GetSize();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            MxcPtySize current = GetSize();
            if (current == previous)
            {
                continue;
            }

            previous = current;
            resize(current);
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "The relay only suppresses reader failures during cancellation teardown.")]
    internal static async Task PumpConsoleInputAsync(
        nint inputHandle,
        Stream destination,
        IMxcConsoleInputApi inputApi,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(inputApi);
        var channel = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
        using var writerLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            channel.Writer.TryWrite([0x03]);
        };
        Console.CancelKeyPress += cancelHandler;

        Task reader = Task.Run(async () =>
        {
            try
            {
                await WindowsMxcConsoleInputReader.PumpAsync(
                    inputHandle,
                    channel.Writer,
                    inputApi,
                    writerLifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);
        Task writer = WriteInputChannelAsync(
            channel.Reader,
            destination,
            writerLifetime.Token);
        try
        {
            Task first = await Task.WhenAny(reader, writer).ConfigureAwait(false);
            await first.ConfigureAwait(false);
            await Task.WhenAll(reader, writer).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await writerLifetime.CancelAsync().ConfigureAwait(false);
            channel.Writer.TryComplete();
            Console.CancelKeyPress -= cancelHandler;
            await ObserveAsync(reader).ConfigureAwait(false);
            await ObserveAsync(writer).ConfigureAwait(false);
        }
    }

    private static async Task PumpPipeInputAsync(
        SafeFileHandle input,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        nint handle = input.DangerousGetHandle();
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Native.PeekNamedPipe(handle, 0, 0, 0, out uint available, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is ErrorBrokenPipe or ErrorNoData)
                {
                    return;
                }
                throw new Win32Exception(error);
            }

            if (available == 0)
            {
                await Task.Delay((int)PollMilliseconds, cancellationToken).ConfigureAwait(false);
                continue;
            }

            uint requested = Math.Min(available, (uint)buffer.Length);
            if (!Native.ReadFile(handle, buffer, requested, out uint read, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error is ErrorBrokenPipe or ErrorNoData)
                {
                    return;
                }
                throw new Win32Exception(error);
            }
            if (read == 0)
            {
                return;
            }
            await destination.WriteAsync(
                buffer.AsMemory(0, (int)read),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PumpDiskInputAsync(
        SafeFileHandle input,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        uint threadId = 0;
        SafeFileHandle duplicate = DuplicateHandle(input);
        Task reader = Task.Factory.StartNew(
            () => ReadDiskInput(duplicate, buffer, destination, cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
        {
            uint id = Volatile.Read(ref threadId);
            if (id == 0)
            {
                return;
            }
            nint thread = Native.OpenThread(ThreadTerminate, false, id);
            if (thread == 0)
            {
                return;
            }
            try
            {
                Native.CancelSynchronousIo(thread);
            }
            finally
            {
                Native.CloseHandle(thread);
            }
        });

        try
        {
            await reader.ConfigureAwait(false);
        }
        finally
        {
            duplicate.Dispose();
        }

        void ReadDiskInput(
            SafeFileHandle handle,
            byte[] bytes,
            Stream target,
            CancellationToken token)
        {
            Volatile.Write(ref threadId, Native.GetCurrentThreadId());
            using var stream = new FileStream(handle, FileAccess.Read, bytes.Length, isAsync: false);
            while (!token.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = stream.Read(bytes, 0, bytes.Length);
                }
                catch (IOException) when (token.IsCancellationRequested)
                {
                    return;
                }
                if (read == 0)
                {
                    return;
                }
                target.Write(bytes, 0, read);
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "An input error is ignored only after the owning process has been canceled.")]
    private static async Task WriteInputChannelAsync(
        System.Threading.Channels.ChannelReader<byte[]> reader,
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] bytes in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await destination.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Pump faults are observed only while unwinding a canceled attached command.")]
    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static SafeFileHandle? DuplicateStandardHandle(uint which)
    {
        nint source = Native.GetStdHandle(which);
        if (source == 0 || source == InvalidHandleValue ||
            !Native.DuplicateHandle(
                Native.GetCurrentProcess(),
                source,
                Native.GetCurrentProcess(),
                out SafeFileHandle duplicate,
                0,
                false,
                DuplicateSameAccess))
        {
            return null;
        }

        return duplicate;
    }

    private static bool IsConsoleHandle(uint which)
    {
        nint handle = Native.GetStdHandle(which);
        return handle != 0 && handle != InvalidHandleValue &&
            Native.GetConsoleMode(handle, out _);
    }

    private static SafeFileHandle DuplicateHandle(SafeFileHandle source)
    {
        if (!Native.DuplicateHandle(
                Native.GetCurrentProcess(),
                source.DangerousGetHandle(),
                Native.GetCurrentProcess(),
                out SafeFileHandle duplicate,
                0,
                false,
                DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return duplicate;
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The captured console state is transferred to the returned restoration scope.")]
    private Scope EnterConsoleInputMode()
    {
        nint input = Native.GetStdHandle(StdInput);
        if (input == 0 || input == InvalidHandleValue)
        {
            return new Scope(static () => { });
        }

        IDisposable capture = WindowsHostConsole.Instance.Capture(_ => { });
        try
        {
            WindowsHostConsole.Instance.InitializeUtf8();
            if (!Native.GetConsoleMode(input, out uint mode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            SetVirtualTerminalInput(input, mode);
            Interlocked.Exchange(ref _inputModeReady, 1);
            return new Scope(() =>
            {
                try
                {
                    Native.SetConsoleMode(input, mode);
                }
                finally
                {
                    Interlocked.Exchange(ref _inputModeReady, 0);
                    capture.Dispose();
                }
            });
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static void SetVirtualTerminalInput(nint input, uint mode)
    {
        uint ptyMode = (mode & ~((uint)EnableLineInput | EnableEchoInput)) |
            EnableVirtualTerminalInput;
        if (!Native.SetConsoleMode(input, ptyMode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The output restoration scope transfers to the enclosing PTY-mode scope.")]
    private static void TryEnableOutputMode(TextWriter output, List<IDisposable> scopes)
    {
        if (WindowsHostConsole.Instance.TryEnableVirtualTerminalProcessing(
                output,
                static _ => { },
                out IDisposable? restore) &&
            restore is not null)
        {
            scopes.Add(restore);
        }
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }

    private const uint StdInput = unchecked((uint)-10);
    private const uint StdOutput = unchecked((uint)-11);
    private const int DuplicateSameAccess = 0x00000002;
    private const int EnableLineInput = 0x0002;
    private const int EnableEchoInput = 0x0004;
    private const int EnableVirtualTerminalInput = 0x0200;
    private const uint PollMilliseconds = 50;
    private const int ErrorBrokenPipe = 109;
    private const int ErrorNoData = 232;
    private const uint FileTypePipe = 3;
    private const uint ThreadTerminate = 0x0001;
    private static readonly nint InvalidHandleValue = new(-1);

    private sealed class NativeConsoleInputApi : IMxcConsoleInputApi
    {
        public static NativeConsoleInputApi Instance { get; } = new();
        public int LastError => Marshal.GetLastWin32Error();
        public uint CurrentThreadId => Native.GetCurrentThreadId();
        public nint OpenThreadForSynchronousCancel(uint threadId) =>
            Native.OpenThread(ThreadTerminate, false, threadId);

        public uint WaitForInput(nint handle, uint milliseconds) =>
            Native.WaitForSingleObject(handle, milliseconds);

        public bool TryReadCharacters(nint handle, byte[] buffer, out uint bytesRead) =>
            Native.ReadFile(handle, buffer, (uint)buffer.Length, out bytesRead, 0);

        public void CancelSynchronousRead(nint threadHandle) =>
            Native.CancelSynchronousIo(threadHandle);

        public void CloseThreadHandle(nint threadHandle) =>
            Native.CloseHandle(threadHandle);
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GetStdHandle(uint standardHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            nint sourceProcess,
            nint sourceHandle,
            nint targetProcess,
            out SafeFileHandle targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int options);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleMode(nint handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleMode(nint handle, uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint WaitForSingleObject(nint handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadFile(
            nint handle,
            [Out] byte[] buffer,
            uint bytesToRead,
            out uint bytesRead,
            nint overlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PeekNamedPipe(
            nint handle,
            nint buffer,
            uint bufferSize,
            nint bytesRead,
            out uint totalBytesAvailable,
            nint bytesLeftThisMessage);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetFileType(nint handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleScreenBufferInfo(
            nint handle,
            out ConsoleScreenBufferInfo info);
        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CancelSynchronousIo(nint thread);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(nint handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleCoordinate
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleScreenBufferInfo
    {
        public ConsoleCoordinate Size;
        public ConsoleCoordinate CursorPosition;
        public ushort Attributes;
        public ConsoleRect Window;
        public ConsoleCoordinate MaximumWindowSize;
    }
}
