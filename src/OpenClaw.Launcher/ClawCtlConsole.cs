using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace OpenClaw.Launcher;

internal static class ClawCtlConsole
{
    private const int Indent = 2;
    private const int FallbackWidth = 100;
    private const string IdentityMark = "\U0001f980";

    private const string IssueUrl =
        "https://github.com/openclaw/openclaw-windows-packaging/issues";

    // Colour is an event, not a wash. Labels stay in the terminal's own
    // foreground -- which is by definition legible on the user's background --
    // and only the state word carries a hue. This mirrors openclaw's
    // styleHealthChannelLine, which colours the leading status word of a
    // "label: detail" row and deliberately leaves the label uncoloured.
    private static readonly Style AccentStyle = new(new Color(0x16, 0x87, 0xff));
    private static readonly Style MutedStyle = new(new Color(0x69, 0x7a, 0x8b));
    private static readonly Style SuccessStyle = new(new Color(0x04, 0x8b, 0x41));
    private static readonly Style WarningStyle = new(new Color(0xb2, 0x65, 0x01));
    private static readonly Style FailureStyle = new(new Color(0xed, 0x18, 0x05));

    private enum StatusKind
    {
        Neutral,
        Success,
        Warning,
        Failure
    }

    internal static void WriteResult(
        TextWriter output,
        IClawCtlResult result,
        bool useColor = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(result);

        var view = new ResultView(
            SupportsUnicode(output),
            result.Command,
            ResolveWidth(output));
        string? literalLine = null;
        switch (result)
        {
            case SetupCommandResult setup:
                WriteSetup(view, setup);
                break;
            case StatusCommandResult status:
                WriteStatus(view, status);
                break;
            case CollectLogsCommandResult logs:
                WriteCollectLogs(view, logs);
                literalLine = logs.Bundle.BundlePath;
                break;
            case TeardownCommandResult teardown:
                WriteTeardown(view, teardown);
                break;
            case GatewayCommandResult gateway:
                WriteGateway(view, gateway);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.GetType().FullName,
                    "Unknown clawctl result type.");
        }

        Render(output, view.Build(), useColor, view.Unicode);
        if (literalLine is not null)
        {
            output.WriteLine(literalLine);
        }
    }

    internal static void WriteVersion(TextWriter output, bool useColor = false)
    {
        ArgumentNullException.ThrowIfNull(output);

        var view = new ResultView(
            SupportsUnicode(output),
            ClawCtlBuildMetadata.PackageVersion,
            ResolveWidth(output));
        view.Row(
            "Package",
            VersionValue(
                ClawCtlBuildMetadata.PackageVersion,
                ClawCtlBuildMetadata.PackageCommit));
        view.Row(
            "Payload",
            VersionValue(
                ClawCtlBuildMetadata.PayloadVersion,
                ClawCtlBuildMetadata.PayloadCommit));
        Render(output, view.Build(), useColor, view.Unicode);
    }

    // The version is what a user compares; the commit is what support pastes
    // into a bug. Keeping the commit on the same row as a muted parenthetical
    // says that without spending a second label on it.
    private static Paragraph VersionValue(string version, string commit)
    {
        var paragraph = new Paragraph();
        paragraph.Append(version);
        paragraph.Append($" ({commit})", MutedStyle);
        return paragraph;
    }

    internal static void WriteHelp(
        TextWriter output,
        ClawCtlHelpModel model,
        bool useColor = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(model);

        var view = new ResultView(
            SupportsUnicode(output),
            model.CommandPath,
            ResolveWidth(output));

        if (!string.IsNullOrWhiteSpace(model.Description))
        {
            foreach (string paragraph in model.Description.Split(
                $"{Environment.NewLine}{Environment.NewLine}",
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                view.Line(paragraph);
                view.Blank();
            }
        }

        view.Row("Usage", new Text(model.Usage));

        if (model.Commands.Count > 0)
        {
            view.Blank();
            view.Line("Commands");
            foreach (ClawCtlHelpEntry entry in model.Commands)
            {
                view.Term(entry.Term, entry.Description);
            }
        }

        if (model.Options.Count > 0)
        {
            view.Blank();
            view.Line("Options");
            foreach (ClawCtlHelpEntry entry in model.Options)
            {
                view.Term(entry.Term, entry.Description);
            }
        }

        Render(output, view.Build(), useColor, view.Unicode);
    }

    internal static void WriteUnexpectedFailure(
        TextWriter error,
        string command,
        string message,
        bool useColor = false)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var view = new ResultView(
            SupportsUnicode(error),
            command,
            ResolveWidth(error));
        view.Note(message);
        view.Blank();
        view.IssueGuidance();
        Render(error, view.Build(), useColor, view.Unicode);
    }

    internal static async Task<T> NarrateAsync<T>(
        TextWriter output,
        bool useColor,
        bool narrate,
        ClawCtlProgress initial,
        Func<IProgress<ClawCtlProgress>, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.Message);

        if (!narrate)
        {
            return await operation(NullProgress.Instance).ConfigureAwait(false);
        }

        if (!IsInteractiveConsole(output))
        {
            await output.WriteLineAsync($"{new string(' ', Indent)}{initial.Message}")
                .ConfigureAwait(false);
            string currentMessage = initial.Message;
            return await operation(new InlineProgress(p =>
            {
                if (!string.Equals(p.Message, currentMessage, StringComparison.Ordinal))
                {
                    WriteProgressLine(output, p.Message);
                    currentMessage = p.Message;
                }
            })).ConfigureAwait(false);
        }

        IAnsiConsole console = CreateConsole(
            output,
            useColor,
            SupportsUnicode(output),
            ResolveWidth(output),
            InteractionSupport.Yes);
        return await NarrateWithStatusAsync(console, initial, operation)
            .ConfigureAwait(false);
    }

    internal static async Task<T> NarrateWithStatusAsync<T>(
        IAnsiConsole console,
        ClawCtlProgress initial,
        Func<IProgress<ClawCtlProgress>, Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(initial);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(initial.Message);

        T result = default!;
        await console.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(AccentStyle)
            .StartAsync(Markup.Escape(initial.Message), async context =>
            {
                result = await operation(new InlineProgress(progress =>
                    context.Status(Markup.Escape(progress.Message))))
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);
        return result;
    }

    private static bool IsInteractiveConsole(TextWriter output) =>
        (ReferenceEquals(output, Console.Out) || ReferenceEquals(output, Console.Error)) &&
        WindowsHostConsole.Instance.IsInteractive;

    private static void WriteProgressLine(TextWriter output, string message) =>
        output.WriteLine($"{new string(' ', Indent)}{message}");

    private sealed class InlineProgress(Action<ClawCtlProgress> report)
        : IProgress<ClawCtlProgress>
    {
        public void Report(ClawCtlProgress value) => report(value);
    }

    private sealed class NullProgress : IProgress<ClawCtlProgress>
    {
        internal static readonly NullProgress Instance = new();

        public void Report(ClawCtlProgress value)
        {
        }
    }

    private static void WriteSetup(ResultView view, SetupCommandResult result)
    {
        if (result.Warning is not null)
        {
            view.Row(
                "Teardown",
                Status(view, StatusKind.Warning, "could not confirm external cleanup"));
            view.Detail(result.Warning.Message);
            if (!string.IsNullOrWhiteSpace(result.Warning.Detail))
            {
                view.Detail(result.Warning.Detail);
            }

            if (result.LocalStateCleared)
            {
                view.Detail("--force was set, so setup continued");
                view.Row(
                    "State",
                    Status(view, StatusKind.Success, "package-local state cleared"));
            }
        }

        view.Row("Package", Status(view, StatusKind.Success, "ready"));
        if (result.Error is not null)
        {
            view.Row("Session", Status(view, StatusKind.Failure, "failed"));
            view.Detail(result.Error);
            return;
        }

        if (result.NodeVersion is not null)
        {
            string runtimeLocation = result.RuntimeLocation switch
            {
                SetupRuntimeLocation.Host => " in the host",
                SetupRuntimeLocation.IsolatedSession => " in the isolated session",
                _ => string.Empty
            };
            view.Row(
                "Runtime",
                Status(
                    view,
                    StatusKind.Success,
                    $"Node.js {result.NodeVersion} installed{runtimeLocation}"));
        }

        if (result.Recovery is not null)
        {
            view.Row(
                "Recovery",
                result.Recovery.State == Gateway.GatewayPersistenceState.Ready
                    ? Status(
                        view,
                        StatusKind.Success,
                        "configured",
                        " - the gateway starts when you sign in")
                    : Status(
                        view,
                        StatusKind.Failure,
                        DescribeRecoveryState(result.Recovery.State)));
            if (!string.IsNullOrWhiteSpace(result.Recovery.Detail))
            {
                view.Detail(result.Recovery.Detail);
            }
        }

        if (result.SessionReady)
        {
            view.Row("Session", Status(view, StatusKind.Success, "ready"));
        }

        if (result.Warning is not null)
        {
            view.Blank();
            view.Note(
                "This machine is not proven clean. Some sandbox and gateway resources " +
                "may still exist, and a later reset cannot remove them, because the " +
                "record of what we owned is gone.");
            view.Blank();
            view.IssueGuidance();
            return;
        }

        if (result.ExitCode == 0)
        {
            view.Blank();
            view.Line("When you're ready, you may want to configure OpenClaw:");
            view.Command("Optional", "openclaw onboard");
        }
    }

    private static void WriteStatus(ResultView view, StatusCommandResult result)
    {
        view.Row("Session", DescribeSession(view, result.Session.Availability));
        if (result.Session.Availability is Session.SessionAvailability.Stale)
        {
            view.Detail("the recorded session is gone from the isolation backend");
        }
        else if (!string.IsNullOrWhiteSpace(result.Session.Detail) &&
            result.Session.Availability is
                Session.SessionAvailability.BackendUnavailable or
                Session.SessionAvailability.BackendError or
                Session.SessionAvailability.Unusable)
        {
            view.Detail(result.Session.Detail);
        }

        if (!string.IsNullOrWhiteSpace(result.NodeVersion))
        {
            view.Row("Runtime", new Text($"Node.js {result.NodeVersion}"));
        }

        view.Row("Gateway", DescribeGateway(view, result.Gateway));
        if (Gateway.GatewayAddress.ResolvePort(result.Gateway.Record) is int statusPort &&
            result.Gateway.State == Gateway.GatewayState.Running)
        {
            view.Row(
                "Port",
                new Text(statusPort.ToString(CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(result.Gateway.Detail))
        {
            view.Detail(result.Gateway.Detail);
        }

        view.Row("Recovery", DescribeRecovery(view, result.Recovery));
        if (!string.IsNullOrWhiteSpace(result.Recovery.Detail))
        {
            view.Detail(result.Recovery.Detail);
        }

        if (result.Session.Availability == Session.SessionAvailability.None)
        {
            view.Blank();
            view.Command("Run", "clawctl setup");
        }
        else if (result.Session.Availability == Session.SessionAvailability.Stale)
        {
            view.Blank();
            view.IssueGuidance();
            view.Blank();
            view.Command("Recover with", "clawctl setup --fresh");
        }
    }

    private static void WriteCollectLogs(ResultView view, CollectLogsCommandResult result)
    {
        if (result.Bundle.BundlePath is null)
        {
            view.Row(
                "Bundle",
                Status(view, StatusKind.Warning, "no diagnostic files were available"));
            return;
        }

        view.Row(
            "Included",
            new Text(result.Bundle.SessionReached
                ? "host and session diagnostics"
                : "host diagnostics only"));
        foreach (string note in result.Bundle.Notes)
        {
            view.Row("Session", Status(view, StatusKind.Warning, note));
        }

        view.Blank();
        view.Line("Redaction is best-effort and targets credential-shaped values.");
        view.Line("Review the bundle before sharing it.");
        view.Blank();
        view.Command("Report an issue", IssueUrl);
        view.Blank();
        view.Line("Bundle:");
    }

    private static void WriteTeardown(ResultView view, TeardownCommandResult result)
    {
        if (!result.Teardown.Succeeded)
        {
            view.Row("Teardown", Status(view, StatusKind.Failure, "failed"));
            view.Detail(result.Teardown.Message);
            if (!string.IsNullOrWhiteSpace(result.Teardown.Detail))
            {
                view.Detail(result.Teardown.Detail);
            }

            return;
        }

        view.Row(
            "Session",
            result.Teardown.SessionRemoved
                ? Status(view, StatusKind.Success, "removed")
                : Status(view, StatusKind.Neutral, "not configured"));
        view.Row("Gateway", Status(view, StatusKind.Success, "records removed"));
        if (result.Teardown.SessionRemoved)
        {
            view.Row("Profile", Status(view, StatusKind.Success, "guest profile deleted"));
        }

        if (!string.IsNullOrWhiteSpace(result.Teardown.Detail))
        {
            view.Detail(result.Teardown.Detail);
        }
    }

    private static void WriteGateway(ResultView view, GatewayCommandResult result)
    {
        view.Row("Gateway", result.State switch
        {
            Gateway.GatewayState.Running => Status(view, StatusKind.Success, "listening"),
            Gateway.GatewayState.NotStarted => Status(view, StatusKind.Neutral, "not started"),

            // Stopping on purpose is a success; a start that ends stopped means
            // the gateway exited while coming up, which is a failure wearing
            // the same state.
            Gateway.GatewayState.Stopped => result.Action switch
            {
                "stop" => Status(view, StatusKind.Success, "stopped"),
                "start" => Status(view, StatusKind.Failure, "exited during startup"),
                _ => Status(view, StatusKind.Neutral, "stopped")
            },
            Gateway.GatewayState.Starting => Status(view, StatusKind.Warning, "starting"),
            Gateway.GatewayState.Unhealthy => Status(view, StatusKind.Failure, "unhealthy"),
            _ => Status(view, StatusKind.Warning, "unknown")
        });

        if (result.Url is not null)
        {
            view.Row("URL", new Text(result.Url));
        }
        else if (result.Port is not null)
        {
            view.Row(
                "Port",
                new Text(result.Port.Value.ToString(CultureInfo.InvariantCulture)));
        }

        // The message explains an outcome the state word cannot. It is
        // redundant once the gateway is listening, because the row already
        // says so, but it is the only account of why a start did not succeed.
        string? explanation = string.IsNullOrWhiteSpace(result.Detail)
            ? result.State == Gateway.GatewayState.Running ? null : result.Message
            : result.Detail;
        if (!string.IsNullOrWhiteSpace(explanation))
        {
            view.Detail(explanation);
        }

        // Reaching the Control UI needs the shared token, and the command that
        // reveals it belongs to OpenClaw rather than to this package.
        if (result.State == Gateway.GatewayState.Running && result.Port is not null)
        {
            view.Blank();
            view.Command("Token", "openclaw gateway auth-token --show");
        }

        if (result.State == Gateway.GatewayState.NotStarted &&
            result.Action == "status")
        {
            view.Blank();
            view.Command("Run", "clawctl gateway-service start");
        }
    }

    private static Paragraph DescribeSession(
        ResultView view,
        Session.SessionAvailability availability) =>
        availability switch
        {
            Session.SessionAvailability.None =>
                Status(view, StatusKind.Neutral, "not configured"),
            Session.SessionAvailability.Running =>
                Status(view, StatusKind.Success, "running"),
            Session.SessionAvailability.Stale =>
                Status(view, StatusKind.Failure, "stale"),
            Session.SessionAvailability.BackendUnavailable =>
                Status(view, StatusKind.Failure, "unavailable"),
            Session.SessionAvailability.BackendError =>
                Status(view, StatusKind.Failure, "failed"),
            Session.SessionAvailability.Unusable =>
                Status(view, StatusKind.Failure, "unusable"),
            _ => Status(view, StatusKind.Warning, "unknown")
        };

    private static Paragraph DescribeGateway(
        ResultView view,
        Gateway.GatewayStatusReport status)
    {
        if (status.State == Gateway.GatewayState.Running)
        {
            IReadOnlyList<int> ports = status.Record?.ObservedPorts ?? [];
            return ports.Count == 1
                ? Status(
                    view,
                    StatusKind.Success,
                    "running",
                    $" on port {ports[0].ToString(CultureInfo.InvariantCulture)}")
                : Status(view, StatusKind.Success, "running");
        }

        return status.State switch
        {
            Gateway.GatewayState.NotStarted =>
                Status(view, StatusKind.Neutral, "not started"),
            Gateway.GatewayState.Stopped =>
                Status(view, StatusKind.Neutral, "stopped"),
            Gateway.GatewayState.Starting =>
                Status(view, StatusKind.Warning, "starting"),
            Gateway.GatewayState.Unhealthy =>
                Status(view, StatusKind.Failure, "unavailable"),
            _ => Status(view, StatusKind.Warning, "unknown")
        };
    }

    private static Paragraph DescribeRecovery(
        ResultView view,
        Gateway.GatewayPersistenceStatus status) =>
        status.State switch
        {
            Gateway.GatewayPersistenceState.Ready =>
                Status(view, StatusKind.Success, "configured"),
            Gateway.GatewayPersistenceState.NotInstalled =>
                Status(view, StatusKind.Neutral, "not configured"),
            Gateway.GatewayPersistenceState.ActionRequired =>
                Status(view, StatusKind.Warning, "needs attention"),
            _ => Status(view, StatusKind.Warning, "unknown")
        };

    private static string DescribeRecoveryState(
        Gateway.GatewayPersistenceState state) =>
        state switch
        {
            Gateway.GatewayPersistenceState.NotInstalled => "not configured",
            Gateway.GatewayPersistenceState.ActionRequired => "needs attention",
            _ => "unknown"
        };

    // A state row is "<mark> <word><trailing>". Only the mark and the state
    // word are coloured; the trailing clause stays in the default foreground
    // so one row never turns into a coloured sentence. Built with Paragraph
    // rather than Markup so caller text is never parsed as markup.
    private static Paragraph Status(
        ResultView view,
        StatusKind kind,
        string word,
        string? trailing = null)
    {
        Style style = kind switch
        {
            StatusKind.Success => SuccessStyle,
            StatusKind.Warning => WarningStyle,
            StatusKind.Failure => FailureStyle,
            _ => MutedStyle
        };

        var paragraph = new Paragraph();
        string? mark = Mark(kind, view.Unicode);
        if (mark is not null)
        {
            paragraph.Append(mark, style);
            paragraph.Append(" ");
        }

        paragraph.Append(word, kind == StatusKind.Neutral ? MutedStyle : style);
        if (!string.IsNullOrEmpty(trailing))
        {
            paragraph.Append(trailing);
        }

        return paragraph;
    }

    private static string? Mark(StatusKind kind, bool unicode) => kind switch
    {
        StatusKind.Success => unicode ? "\u2713" : "[ok]",
        StatusKind.Warning => unicode ? "!" : "[!]",
        StatusKind.Failure => unicode ? "\u2717" : "[x]",
        _ => null
    };

    internal static string FormatHeading(string command, bool useUnicode) =>
        useUnicode
            ? $"{IdentityMark} clawctl {command}"
            : $"clawctl {command}";

    private static void Render(
        TextWriter output,
        IRenderable renderable,
        bool useColor,
        bool unicode)
    {
        var buffer = new StringWriter(CultureInfo.InvariantCulture);
        IAnsiConsole console = CreateConsole(
            buffer,
            useColor,
            unicode,
            ResolveWidth(output),
            InteractionSupport.No);
        console.Write(renderable);

        // Grid pads every cell to its column width, so rows arrive with
        // trailing spaces. Trimming keeps redirected output byte-clean and
        // applies identically to the coloured and plain renderings, which is
        // what keeps the two textually equal once ANSI is stripped.
        string[] lines = buffer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd();
            if (index == lines.Length - 1 && line.Length == 0)
            {
                continue;
            }

            output.WriteLine(line);
        }
    }

    private static IAnsiConsole CreateConsole(
        TextWriter target,
        bool useColor,
        bool unicode,
        int width,
        InteractionSupport interaction)
    {
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = useColor ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = useColor ? ColorSystemSupport.TrueColor : ColorSystemSupport.NoColors,
            Interactive = interaction,
            Out = new AnsiConsoleOutput(target),
        });
        console.Profile.Width = width;
        console.Profile.Capabilities.Unicode = unicode;
        return console;
    }

    private static int ResolveWidth(TextWriter output)
    {
        if (!ReferenceEquals(output, Console.Out) && !ReferenceEquals(output, Console.Error))
        {
            return FallbackWidth;
        }

        try
        {
            int width = Console.WindowWidth;
            return width > 20 ? width : FallbackWidth;
        }
        catch (Exception exception) when (
            exception is IOException or PlatformNotSupportedException or
            ArgumentOutOfRangeException)
        {
            return FallbackWidth;
        }
    }

    private static string LowercaseFirst(string value) =>
        value.Length == 0 ||
        (value.Length > 1 && char.IsUpper(value[0]) && char.IsUpper(value[1]))
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private static bool SupportsUnicode(TextWriter output) =>
        WindowsHostConsole.Instance.IsInteractiveOutput(output) &&
        Console.OutputEncoding.CodePage == 65001;

    // Collects the whole result before rendering so one grid can own every
    // row. Fixing the label column from the widest label is what replaces the
    // per-command magic widths the string renderer needed.
    private sealed class ResultView(bool unicode, string command, int width)
    {
        // openclaw caps a note at min(88, columns - 10) rather than letting it
        // span the terminal, and a full-width box reads as a wall. Match it.
        private const int MaxNoteWidth = 88;

        private readonly List<Item> _items = [];

        public bool Unicode { get; } = unicode;

        public void Row(string label, IRenderable value) =>
            _items.Add(Item.ForRow($"{label}:", new Text($"{label}:"), value));

        // A help term is something the user types, so it takes the same accent
        // as a next-action command rather than a label's plain foreground.
        public void Term(string term, string? description) =>
            _items.Add(Item.ForRow(
                term,
                new Text(term, AccentStyle),
                description is null ? Text.Empty : new Text(description)));

        public void Command(string label, string command) =>
            Row(label, new Text(command, AccentStyle));

        public void Detail(string detail)
        {
            foreach (string line in detail.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _items.Add(Item.ForRow(
                    string.Empty,
                    Text.Empty,
                    new Text(LowercaseFirst(line), MutedStyle)));
            }
        }

        public void Blank() => _items.Add(Item.ForFree(Text.Empty));

        public void Line(string text) => _items.Add(Item.ForFree(new Text(text)));

        public void Note(string message) =>
            _items.Add(Item.ForFree(new Panel(new Text(message))
            {
                Header = new PanelHeader(" note "),
                Border = Unicode ? BoxBorder.Rounded : BoxBorder.Ascii,
                BorderStyle = FailureStyle,
                Padding = new Padding(1, 0, 1, 0),
                Width = Math.Max(40, Math.Min(MaxNoteWidth, width - Indent)),
            }));

        public void IssueGuidance()
        {
            Line("This shouldn't happen. Please report it so we can fix the cause:");
            Line("  1. clawctl collect-logs");
            Line($"  2. {IssueUrl}");
        }

        public Rows Build()
        {
            int labelWidth = 0;
            foreach (Item item in _items)
            {
                if (item.ColumnText is not null)
                {
                    labelWidth = Math.Max(labelWidth, item.ColumnText.Length + 1);
                }
            }

            var blocks = new List<IRenderable>();
            Grid? grid = null;
            foreach (Item item in _items)
            {
                if (item.Free is not null)
                {
                    grid = null;
                    blocks.Add(item.Free);
                    continue;
                }

                if (grid is null)
                {
                    grid = new Grid();
                    grid.AddColumn(new GridColumn().NoWrap().Width(labelWidth + 1));
                    grid.AddColumn(new GridColumn().PadRight(0));
                    blocks.Add(grid);
                }

                grid.AddRow(item.Column!, item.Value!);
            }

            var heading = new Paragraph();
            if (Unicode)
            {
                heading.Append($"{IdentityMark} ");
            }

            heading.Append(HostEntrypointResolver.ControlCommandName, MutedStyle);
            if (!string.IsNullOrEmpty(command))
            {
                heading.Append(" ");
                heading.Append(command, AccentStyle);
            }

            return new Rows(
                heading,
                Text.Empty,
                new Padder(new Rows(blocks), new Padding(Indent, 0, 0, 0)));
        }

        private readonly struct Item
        {
            private Item(string? columnText, IRenderable? column, IRenderable? value, IRenderable? free)
            {
                ColumnText = columnText;
                Column = column;
                Value = value;
                Free = free;
            }

            public string? ColumnText { get; }

            public IRenderable? Column { get; }

            public IRenderable? Value { get; }

            public IRenderable? Free { get; }

            public static Item ForRow(string columnText, IRenderable column, IRenderable value) =>
                new(columnText, column, value, null);

            public static Item ForFree(IRenderable free) => new(null, null, null, free);
        }
    }
}
