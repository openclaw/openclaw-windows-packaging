using System.Runtime.InteropServices;
using System.Text;

namespace OpenClaw.Launcher;

internal interface IHostConsole
{
    IDisposable Capture(Action<string> log);

    void InitializeUtf8();

    bool IsInteractive { get; }
}

internal interface IConsoleNativeApi
{
    nint GetStdHandle(uint standardHandle);
    bool GetConsoleMode(nint handle, out uint mode);
    bool SetConsoleMode(nint handle, uint mode);
    uint GetConsoleCP();
    uint GetConsoleOutputCP();
    bool SetConsoleCP(uint codePage);
    bool SetConsoleOutputCP(uint codePage);
    bool WriteConsole(nint handle, string text);
}

internal interface IConsoleEncoding
{
    Encoding InputEncoding { get; set; }

    Encoding OutputEncoding { get; set; }
}

internal sealed class WindowsHostConsole : IHostConsole
{
    private const uint StdInput = unchecked(0xFFFFFFF6);
    private const uint StdOutput = unchecked(0xFFFFFFF5);
    private const uint StdError = unchecked(0xFFFFFFF4);
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint InvalidHandle = unchecked(0xFFFFFFFF);
    private const string ResetSequence =
        "\u001b[?9001l\u001b[?2004l\u001b[?1000l\u001b[?1002l" +
        "\u001b[?1003l\u001b[?1006l\u001b[?1015l\u001b[?1004l" +
        "\u001b[?7h\u001b[?25h\u001b[0m";

    internal static WindowsHostConsole Instance { get; } = new();

    private readonly IConsoleNativeApi _native;
    private readonly IConsoleEncoding _encoding;

    internal WindowsHostConsole(
        IConsoleNativeApi? native = null,
        IConsoleEncoding? encoding = null)
    {
        _native = native ?? new ConsoleNativeApi();
        _encoding = encoding ?? new ConsoleEncoding();
    }

    public bool IsInteractive
    {
        get
        {
            nint output = _native.GetStdHandle(StdOutput);
            return output != 0 && output != InvalidHandle &&
                _native.GetConsoleMode(output, out _);
        }
    }

    public void InitializeUtf8()
    {
        TrySetCodePage(_native.SetConsoleCP, 65001);
        TrySetCodePage(_native.SetConsoleOutputCP, 65001);
        TrySetEncoding(
            encoding => _encoding.InputEncoding = encoding,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TrySetEncoding(
            encoding => _encoding.OutputEncoding = encoding,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public IDisposable Capture(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new ConsoleCapture(_native, _encoding, log);
    }

    private static void TrySetCodePage(Func<uint, bool> setter, uint codePage)
    {
        try
        {
            setter(codePage);
        }
        catch (Exception exception) when (exception is IOException or
            InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void TrySetEncoding(Action<Encoding> setter, Encoding encoding)
    {
        try
        {
            setter(encoding);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException)
        {
        }
    }

    private sealed class ConsoleCapture : IDisposable
    {
        private readonly IConsoleNativeApi _native;
        private readonly IConsoleEncoding _encoding;
        private readonly Action<string> _log;
        private readonly uint _inputMode;
        private readonly List<(nint Handle, uint Mode)> _outputModes = [];
        private readonly uint _inputCodePage;
        private readonly uint _outputCodePage;
        private readonly Encoding _inputEncoding;
        private readonly Encoding _outputEncoding;
        private readonly nint _cleanupOutput;
        private int _disposed;

        public ConsoleCapture(
            IConsoleNativeApi native,
            IConsoleEncoding encoding,
            Action<string> log)
        {
            _native = native;
            _encoding = encoding;
            _log = log;
            _inputCodePage = native.GetConsoleCP();
            _outputCodePage = native.GetConsoleOutputCP();
            _inputEncoding = encoding.InputEncoding;
            _outputEncoding = encoding.OutputEncoding;
            nint input = native.GetStdHandle(StdInput);
            if (native.GetConsoleMode(input, out uint inputMode))
            {
                _inputMode = inputMode;
                HasInput = true;
            }

            foreach (nint output in new[] {
                native.GetStdHandle(StdOutput), native.GetStdHandle(StdError) })
            {
                if (output == 0 || output == InvalidHandle ||
                    !native.GetConsoleMode(output, out uint mode))
                {
                    continue;
                }

                _outputModes.Add((output, mode));
                if (_cleanupOutput == 0)
                {
                    _cleanupOutput = output;
                }
            }
        }

        private bool HasInput { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_cleanupOutput != 0)
            {
                try
                {
                    _native.GetConsoleMode(_cleanupOutput, out uint current);
                    _native.SetConsoleMode(
                        _cleanupOutput,
                        current | EnableProcessedOutput | EnableVirtualTerminalProcessing);
                    _native.WriteConsole(_cleanupOutput, ResetSequence);
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
                {
                    _log($"Console cleanup failed: {exception.Message}");
                }
            }

            if (HasInput)
            {
                TryRestore(
                    "input mode",
                    () => _native.SetConsoleMode(
                        _native.GetStdHandle(StdInput), _inputMode));
            }

            foreach ((nint handle, uint mode) in _outputModes)
            {
                TryRestore(
                    "output mode",
                    () => _native.SetConsoleMode(handle, mode));
            }

            // Console's managed encoding setters update the native code page.
            // Restore them first so the captured code pages remain authoritative.
            TryRestore("input encoding", () => _encoding.InputEncoding = _inputEncoding);
            TryRestore("output encoding", () => _encoding.OutputEncoding = _outputEncoding);
            TryRestore("input code page", () =>
            {
                if (_inputCodePage != 0)
                {
                    _native.SetConsoleCP(_inputCodePage);
                }
            });
            TryRestore("output code page", () =>
            {
                if (_outputCodePage != 0)
                {
                    _native.SetConsoleOutputCP(_outputCodePage);
                }
            });
        }

        private void TryRestore(string name, Action restore)
        {
            try
            {
                restore();
            }
            catch (Exception exception) when (exception is IOException or
                InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _log($"Console {name} restoration failed: {exception.Message}");
            }
        }
    }

    private sealed class ConsoleNativeApi : IConsoleNativeApi
    {
        public nint GetStdHandle(uint standardHandle) => Native.GetStdHandle(standardHandle);
        public bool GetConsoleMode(nint handle, out uint mode) =>
            Native.GetConsoleMode(handle, out mode);
        public bool SetConsoleMode(nint handle, uint mode) =>
            Native.SetConsoleMode(handle, mode);
        public uint GetConsoleCP() => Native.GetConsoleCP();
        public uint GetConsoleOutputCP() => Native.GetConsoleOutputCP();
        public bool SetConsoleCP(uint codePage) => Native.SetConsoleCP(codePage);
        public bool SetConsoleOutputCP(uint codePage) =>
            Native.SetConsoleOutputCP(codePage);
        public bool WriteConsole(nint handle, string text) =>
            Native.WriteConsole(handle, text, text.Length, out _, 0);
    }

    private sealed class ConsoleEncoding : IConsoleEncoding
    {
        public Encoding InputEncoding
        {
            get => Console.InputEncoding;
            set => Console.InputEncoding = value;
        }

        public Encoding OutputEncoding
        {
            get => Console.OutputEncoding;
            set => Console.OutputEncoding = value;
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern nint GetStdHandle(uint standardHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetConsoleMode(nint handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleMode(nint handle, uint mode);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetConsoleCP();
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleCP(uint codePage);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetConsoleOutputCP(uint codePage);
        [DllImport("kernel32.dll", EntryPoint = "WriteConsoleW", SetLastError = true,
            CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WriteConsole(
            nint handle, string text, int chars, out int written, nint reserved);
    }
}
