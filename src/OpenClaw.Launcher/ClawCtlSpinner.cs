using Spectre.Console;

namespace OpenClaw.Launcher;

internal static class ClawCtlSpinner
{
    internal static Spinner Scuttle { get; } = new FrameSpinner(
        TimeSpan.FromMilliseconds(120),
        isUnicode: true,
        [
            "\U0001f980\u00b7\u00b7\u00b7",
            "\u00b7\U0001f980\u00b7\u00b7",
            "\u00b7\u00b7\U0001f980\u00b7",
            "\u00b7\u00b7\u00b7\U0001f980",
            "\u00b7\u00b7\U0001f980\u00b7",
            "\u00b7\U0001f980\u00b7\u00b7",
        ]);

    internal static Spinner Bubbles { get; } = new FrameSpinner(
        TimeSpan.FromMilliseconds(120),
        isUnicode: true,
        [
            "\U0001f980   ",
            "\U0001f980\u00b7  ",
            "\U0001f980\u00b0  ",
            "\U0001f980 \u00b0 ",
            "\U0001f980  \u00b0",
            "\U0001f980   ",
        ]);

    internal static Spinner TidePulse { get; } = new FrameSpinner(
        TimeSpan.FromMilliseconds(120),
        isUnicode: true,
        [
            "  \U0001f980  ",
            "\u00b7 \U0001f980 \u00b7",
            "\u00b7\u00b7\U0001f980\u00b7\u00b7",
            "\u00b7 \U0001f980 \u00b7",
        ]);

    internal static Spinner Ascii { get; } = new FrameSpinner(
        TimeSpan.FromMilliseconds(160),
        isUnicode: false,
        [
            "v(.-.)v",
            "V(.-.)V",
        ]);

    private static readonly Spinner[] UnicodeSpinners =
    [
        Scuttle,
        Bubbles,
        TidePulse,
    ];

    internal static Spinner Select(bool supportsUnicode) =>
        Select(supportsUnicode, Random.Shared.Next);

    internal static Spinner Select(
        bool supportsUnicode,
        Func<int, int> chooseIndex)
    {
        ArgumentNullException.ThrowIfNull(chooseIndex);
        return supportsUnicode
            ? UnicodeSpinners[chooseIndex(UnicodeSpinners.Length)]
            : Ascii;
    }

    private sealed class FrameSpinner(
        TimeSpan interval,
        bool isUnicode,
        IReadOnlyList<string> frames) : Spinner
    {
        public override TimeSpan Interval { get; } = interval;

        public override bool IsUnicode { get; } = isUnicode;

        public override IReadOnlyList<string> Frames { get; } = frames;
    }
}
