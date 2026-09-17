using System.Text;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Gateway;

/// <summary>Writes gateway command results without trusting guest-produced log text.</summary>
internal static class GatewayControlOutput
{
    private const int MaximumLogTailBytes = 16 * 1024;
    private const int LogTailLineCount = 10;

    public static async Task WriteStatusAsync(
        TextWriter output,
        GatewayStatusReport result,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(result.Message).ConfigureAwait(false);
        await WriteDetailAsync(output, result.Detail).ConfigureAwait(false);
        if (result.State is GatewayState.Stopped or GatewayState.Unhealthy)
        {
            await WriteLogTailAsync(
                output,
                workspacePath,
                result.Record?.LogPath,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task WriteStopAsync(
        TextWriter output,
        GatewayStopResult result)
    {
        await output.WriteLineAsync(result.Message).ConfigureAwait(false);
        await WriteDetailAsync(output, result.Detail).ConfigureAwait(false);
    }

    private static async Task WriteDetailAsync(TextWriter output, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            await output.WriteLineAsync(detail).ConfigureAwait(false);
        }
    }

    private static async Task WriteLogTailAsync(
        TextWriter output,
        string? workspacePath,
        string? logPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) ||
            string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            using FileStream stream = TrustedPath.OpenRead(workspacePath, logPath);
            if (stream.Length == 0)
            {
                return;
            }

            stream.Seek(Math.Max(0, stream.Length - MaximumLogTailBytes), SeekOrigin.Begin);
            byte[] bytes = new byte[checked((int)Math.Min(MaximumLogTailBytes, stream.Length - stream.Position))];
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            string text = Encoding.UTF8.GetString(bytes, 0, offset);
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] tail =
            [
                .. lines.TakeLast(LogTailLineCount)
                    .Select(Sanitize)
                    .Where(line => line.Length > 0)
            ];
            if (tail.Length == 0)
            {
                return;
            }

            await output.WriteLineAsync($"Gateway log tail ({logPath}):").ConfigureAwait(false);
            foreach (string line in tail)
            {
                await output.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SessionException)
        {
            // A guest-writable diagnostic must not change a status command's result.
            await output.WriteLineAsync($"Gateway log unavailable: {logPath}").ConfigureAwait(false);
        }
    }

    private static string Sanitize(string value)
    {
        StringBuilder builder = new(value.Length);
        bool afterEscape = false;
        bool inControlSequence = false;
        foreach (char character in value)
        {
            if (afterEscape)
            {
                inControlSequence = character == '[';
                afterEscape = false;
                continue;
            }

            if (inControlSequence)
            {
                if (character is >= '@' and <= '~')
                {
                    inControlSequence = false;
                }

                continue;
            }

            if (character == '\x1b')
            {
                afterEscape = true;
            }
            else if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
