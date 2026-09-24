using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher;

/// <summary>
/// Describes a failure for the host diagnostic log.
/// </summary>
/// <remarks>
/// Users are shown <see cref="Exception.Message"/>. The log is what reaches a
/// bug report, so it also keeps what a message drops: the exception type, the
/// classification a backend reported, the Windows error code behind an I/O or
/// native failure, and every inner cause.
/// </remarks>
internal static class DiagnosticFailure
{
    // Deeper than any wrapper chain this host builds; the bound only stops a
    // pathological chain from growing a log entry without limit.
    private const int MaximumDepth = 8;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new StringBuilder();
        Append(builder, exception, depth: 0);
        return builder.ToString();
    }

    /// <summary>
    /// Describes a failure no handler anticipated, followed by the stack trace
    /// of every exception among its causes so each throw site survives.
    /// </summary>
    public static string DescribeWithStackTrace(Exception exception)
    {
        var builder = new StringBuilder(Describe(exception));
        bool wroteFrames = false;
        AppendStackTraces(builder, exception, depth: 0, ref wroteFrames);
        return builder.ToString();
    }

    // Each cause's frames come before those of the exception that wraps it,
    // as Exception.ToString() lays out a chain: the frames closest to the
    // original fault lead. Aggregated siblings are all visited, in order.
    private static void AppendStackTraces(
        StringBuilder builder,
        Exception exception,
        int depth,
        ref bool wroteFrames)
    {
        if (depth + 1 < MaximumDepth)
        {
            foreach (Exception cause in CausesOf(exception))
            {
                AppendStackTraces(builder, cause, depth + 1, ref wroteFrames);
            }
        }

        if (string.IsNullOrWhiteSpace(exception.StackTrace))
        {
            return;
        }

        if (wroteFrames)
        {
            builder.AppendLine();
            builder.Append("   --- End of inner exception stack trace ---");
        }

        builder.AppendLine();
        builder.Append(exception.StackTrace.TrimEnd());
        wroteFrames = true;
    }

    private static ReadOnlyCollection<Exception> CausesOf(Exception exception) =>
        exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : new ReadOnlyCollection<Exception>(
                exception.InnerException is { } inner ? [inner] : []);

    private static void Append(StringBuilder builder, Exception exception, int depth)
    {
        builder.Append(exception.GetType().Name);
        string message = SingleLine(exception.Message);
        if (message.Length > 0)
        {
            builder.Append(": ").Append(message);
        }

        if (DescribeCodes(exception) is { } codes)
        {
            builder.Append(" [").Append(codes).Append(']');
        }

        ReadOnlyCollection<Exception> causes = CausesOf(exception);
        if (causes.Count == 0)
        {
            return;
        }

        builder.Append(" ---> ");
        if (depth + 1 >= MaximumDepth)
        {
            builder.Append("...");
            return;
        }

        if (causes.Count == 1)
        {
            Append(builder, causes[0], depth + 1);
            return;
        }

        // Numbered so sibling causes cannot be read as one nested chain.
        builder.Append(string.Create(CultureInfo.InvariantCulture, $"{causes.Count} causes: "));
        for (int index = 0; index < causes.Count; index++)
        {
            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{(index == 0 ? string.Empty : "; ")}({index + 1}) "));
            Append(builder, causes[index], depth + 1);
        }
    }

    private static string? DescribeCodes(Exception exception) =>
        exception switch
        {
            MxcException mxc => string.Join(
                "; ",
                new[]
                {
                    $"code {mxc.Code}",
                    mxc.BackendCode is { Length: > 0 } backendCode
                        ? $"backend code {backendCode}"
                        : null,
                    mxc.Operation is { Length: > 0 } operation
                        ? $"operation {operation}"
                        : null
                }.Where(static part => part is not null)),
            SessionStateException state => $"fault {state.Fault}",

            // Before ExternalException: Win32Exception's HRESULT is usually
            // the generic E_FAIL, while the native error names the fault.
            Win32Exception win32 => string.Create(
                CultureInfo.InvariantCulture,
                $"Win32 error {win32.NativeErrorCode}"),
            IOException or UnauthorizedAccessException or ExternalException =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"HRESULT 0x{exception.HResult:X8}"),
            _ => null
        };

    /// <summary>
    /// Keeps text on its log entry's line.
    /// </summary>
    /// <remarks>
    /// Messages and details can carry text the guest wrote. Collapsing line
    /// breaks and other control characters to single spaces stops that text
    /// from forging an entry of its own or carrying terminal escape sequences
    /// into the log. Only an unhandled failure's stack trace continues onto
    /// the lines after its entry.
    /// </remarks>
    internal static string SingleLine(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char character in text)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
