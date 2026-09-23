using Spectre.Console;

namespace OpenClaw.Launcher.Tests;

public sealed class ClawCtlSpinnerTests
{
    [Fact]
    public void UnicodeSelectionIncludesEveryCrabAnimation()
    {
        Assert.Same(ClawCtlSpinner.Scuttle, ClawCtlSpinner.Select(true, _ => 0));
        Assert.Same(ClawCtlSpinner.Bubbles, ClawCtlSpinner.Select(true, _ => 1));
        Assert.Same(ClawCtlSpinner.TidePulse, ClawCtlSpinner.Select(true, _ => 2));
        Assert.Equal(
            [
                "\U0001f980\u00b7\u00b7\u00b7",
                "\u00b7\U0001f980\u00b7\u00b7",
                "\u00b7\u00b7\U0001f980\u00b7",
                "\u00b7\u00b7\u00b7\U0001f980",
                "\u00b7\u00b7\U0001f980\u00b7",
                "\u00b7\U0001f980\u00b7\u00b7",
            ],
            ClawCtlSpinner.Scuttle.Frames);
        Assert.Equal(
            [
                "\U0001f980   ",
                "\U0001f980\u00b7  ",
                "\U0001f980\u00b0  ",
                "\U0001f980 \u00b0 ",
                "\U0001f980  \u00b0",
                "\U0001f980   ",
            ],
            ClawCtlSpinner.Bubbles.Frames);
        Assert.Equal(
            [
                "  \U0001f980  ",
                "\u00b7 \U0001f980 \u00b7",
                "\u00b7\u00b7\U0001f980\u00b7\u00b7",
                "\u00b7 \U0001f980 \u00b7",
            ],
            ClawCtlSpinner.TidePulse.Frames);
    }

    [Fact]
    public void NonUnicodeSelectionUsesCrabEmoticonWithoutRandomChoice()
    {
        Spinner selected = ClawCtlSpinner.Select(
            supportsUnicode: false,
            _ => throw new InvalidOperationException("The fallback is not random."));

        Assert.Same(ClawCtlSpinner.Ascii, selected);
        Assert.False(selected.IsUnicode);
        Assert.Equal(["v(.-.)v", "V(.-.)V"], selected.Frames);
    }

    [Fact]
    public void UnicodeAnimationsHaveStableFrameWidths()
    {
        AssertStableWidth(ClawCtlSpinner.Scuttle);
        AssertStableWidth(ClawCtlSpinner.Bubbles);
        AssertStableWidth(ClawCtlSpinner.TidePulse);
    }

    private static void AssertStableWidth(Spinner spinner)
    {
        Assert.True(spinner.IsUnicode);
        Assert.Single(spinner.Frames
            .Select(static frame => new Text(frame).Length)
            .Distinct());
    }
}
