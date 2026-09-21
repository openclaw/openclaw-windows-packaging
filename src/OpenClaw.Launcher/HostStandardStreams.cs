namespace OpenClaw.Launcher;

/// <summary>
/// This process's standard streams, as raw byte streams.
/// </summary>
/// <remarks>
/// Exists so the piped-input path can be driven by a test without a real
/// console. Only the launch path that redirects the MXC executor's streams
/// needs these; the ordinary attached path inherits the handles instead and
/// never opens them.
/// </remarks>
internal interface IHostStandardStreams
{
    Stream OpenInput();

    Stream OpenOutput();

    Stream OpenError();
}

internal sealed class ProcessStandardStreams : IHostStandardStreams
{
    internal static ProcessStandardStreams Instance { get; } = new();

    public Stream OpenInput() => Console.OpenStandardInput();

    public Stream OpenOutput() => Console.OpenStandardOutput();

    public Stream OpenError() => Console.OpenStandardError();
}
