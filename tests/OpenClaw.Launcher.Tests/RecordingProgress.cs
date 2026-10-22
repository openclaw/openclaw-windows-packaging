namespace OpenClaw.Launcher.Tests;

/// <summary>
/// Captures narration synchronously, in the order the operation reported it.
/// </summary>
internal sealed class RecordingProgress : IProgress<ClawCtlProgress>
{
    public List<string> Messages { get; } = [];

    public void Report(ClawCtlProgress value) => Messages.Add(value.Message);
}
