using System.Text;

namespace OpenClaw.Launcher.Tests;

public sealed class WindowsHostConsoleTests
{
    [Fact]
    public void CaptureRestoresModesCodePagesAndOnlyWritesVerifiedCleanup()
    {
        var native = new FakeConsoleNativeApi
        {
            InputMode = 0x11,
            OutputMode = 0x04 | 0x20,
            InputCodePage = 437,
            OutputCodePage = 1252
        };
        var encoding = new FakeConsoleEncoding
        {
            InputEncoding = Encoding.ASCII,
            OutputEncoding = Encoding.ASCII
        };
        var console = new WindowsHostConsole(native, encoding);

        IDisposable capture = console.Capture(_ => { });
        console.InitializeUtf8();
        native.InputMode = 0;
        native.OutputMode = 0x04;
        native.InputCodePage = 65001;
        native.OutputCodePage = 65001;
        capture.Dispose();
        capture.Dispose();

        Assert.Equal(0x11u, native.InputMode);
        Assert.Equal(0x24u, native.OutputMode);
        Assert.Equal(437u, native.InputCodePage);
        Assert.Equal(1252u, native.OutputCodePage);
        Assert.Equal(Encoding.ASCII.CodePage, encoding.InputEncoding.CodePage);
        Assert.Equal(Encoding.ASCII.CodePage, encoding.OutputEncoding.CodePage);
        Assert.Single(native.Writes);
        Assert.DoesNotContain(
            "?1049l",
            native.Writes[0],
            StringComparison.Ordinal);
        Assert.Equal(
            "\u001b[?9001l\u001b[?2004l\u001b[?1000l\u001b[?1002l" +
            "\u001b[?1003l\u001b[?1006l\u001b[?1015l\u001b[?1004l" +
            "\u001b[?7h\u001b[?25h\u001b[0m",
            native.Writes[0]);
    }

    [Fact]
    public void CaptureDoesNotWriteCleanupForRedirectedOutputOrZeroCodePages()
    {
        var native = new FakeConsoleNativeApi
        {
            InputCodePage = 0,
            OutputCodePage = 0,
            OutputIsConsole = false
        };
        var console = new WindowsHostConsole(native);

        using (console.Capture(_ => { }))
        {
        }

        Assert.Empty(native.Writes);
        Assert.DoesNotContain(native.SetModes, entry => entry.Handle != 1);
        Assert.Empty(native.SetCodePages);
    }

    [Fact]
    public void InitializeUtf8ToleratesEncodingSettersWithoutConsoleHandles()
    {
        var encoding = new FakeConsoleEncoding
        {
            InputEncoding = Encoding.ASCII,
            OutputEncoding = Encoding.ASCII,
            ThrowWhenSet = true
        };
        var console = new WindowsHostConsole(new FakeConsoleNativeApi(), encoding);

        console.InitializeUtf8();

        Assert.Equal(Encoding.ASCII.CodePage, encoding.InputEncoding.CodePage);
        Assert.Equal(Encoding.ASCII.CodePage, encoding.OutputEncoding.CodePage);
    }

    [Fact]
    public void CaptureContinuesRestoringCodePagesWhenEncodingRestorationFails()
    {
        var native = new FakeConsoleNativeApi
        {
            InputCodePage = 437,
            OutputCodePage = 1252
        };
        var encoding = new FakeConsoleEncoding
        {
            InputEncoding = Encoding.ASCII,
            OutputEncoding = Encoding.ASCII
        };
        var console = new WindowsHostConsole(native, encoding);

        using (console.Capture(_ => { }))
        {
            console.InitializeUtf8();
            encoding.ThrowWhenSet = true;
        }

        Assert.Equal(437u, native.InputCodePage);
        Assert.Equal(1252u, native.OutputCodePage);
    }

    private sealed class FakeConsoleNativeApi : IConsoleNativeApi
    {
        public uint InputMode { get; set; }
        public uint OutputMode { get; set; }
        public uint InputCodePage { get; set; }
        public uint OutputCodePage { get; set; }
        public bool OutputIsConsole { get; set; } = true;
        public List<string> Writes { get; } = [];
        public List<(nint Handle, uint Mode)> SetModes { get; } = [];
        public List<uint> SetCodePages { get; } = [];

        public nint GetStdHandle(uint standardHandle) => standardHandle switch
        {
            0xFFFFFFF6 => 1,
            0xFFFFFFF5 => 2,
            0xFFFFFFF4 => 3,
            _ => 0
        };

        public bool GetConsoleMode(nint handle, out uint mode)
        {
            if (handle == 1)
            {
                mode = InputMode;
                return true;
            }

            mode = OutputMode;
            return OutputIsConsole;
        }

        public bool SetConsoleMode(nint handle, uint mode)
        {
            SetModes.Add((handle, mode));
            if (handle == 1)
            {
                InputMode = mode;
            }
            else
            {
                OutputMode = mode;
            }

            return true;
        }

        public uint GetConsoleCP() => InputCodePage;
        public uint GetConsoleOutputCP() => OutputCodePage;
        public bool SetConsoleCP(uint codePage)
        {
            SetCodePages.Add(codePage);
            InputCodePage = codePage;
            return true;
        }

        public bool SetConsoleOutputCP(uint codePage)
        {
            SetCodePages.Add(codePage);
            OutputCodePage = codePage;
            return true;
        }

        public bool WriteConsole(nint handle, string text)
        {
            Writes.Add(text);
            return true;
        }

    }

    private sealed class FakeConsoleEncoding : IConsoleEncoding
    {
        private Encoding _inputEncoding = Encoding.ASCII;
        private Encoding _outputEncoding = Encoding.ASCII;

        public bool ThrowWhenSet { get; set; }

        public required Encoding InputEncoding
        {
            get => _inputEncoding;
            set => _inputEncoding = ThrowWhenSet
                ? throw new IOException("No console input handle.")
                : value;
        }

        public required Encoding OutputEncoding
        {
            get => _outputEncoding;
            set => _outputEncoding = ThrowWhenSet
                ? throw new IOException("No console output handle.")
                : value;
        }
    }
}
