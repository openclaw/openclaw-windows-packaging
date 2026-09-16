using System.Runtime.InteropServices;

namespace OpenClaw.Launcher;

internal enum HostEntrypoint
{
    Agent,
    Control
}

internal static class HostEntrypointResolver
{
    internal const string AgentCommandName = "openclaw";
    internal const string ControlCommandName = "clawctl";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();

    public static HostEntrypoint Resolve() =>
        Resolve(TryGetNativeCommandLine(), PackageIdentity.TryGetApplicationUserModelId());

    internal static HostEntrypoint Resolve(
        string? commandLine,
        string? applicationUserModelId = null)
    {
        if (applicationUserModelId?.EndsWith(
            "!" + Gateway.GatewayLauncherScript.ControlApplicationId,
            StringComparison.OrdinalIgnoreCase) == true)
        {
            return HostEntrypoint.Control;
        }

        return TryMatch(GetInvokedName(commandLine), out HostEntrypoint invoked)
            ? invoked
            : HostEntrypoint.Agent;
    }

    internal static string? GetInvokedName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        ReadOnlySpan<char> span = commandLine.AsSpan().TrimStart();
        ReadOnlySpan<char> first;
        if (span[0] == '"')
        {
            int closing = span[1..].IndexOf('"');
            if (closing < 0)
            {
                return null;
            }

            first = span.Slice(1, closing);
        }
        else
        {
            int end = span.IndexOf(' ');
            first = end < 0 ? span : span[..end];
        }

        if (first.IsEmpty)
        {
            return null;
        }

        try
        {
            string name = Path.GetFileNameWithoutExtension(first.ToString());
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool TryMatch(
        string? name,
        out HostEntrypoint entrypoint)
    {
        if (string.Equals(
            name?.Trim(),
            ControlCommandName,
            StringComparison.OrdinalIgnoreCase))
        {
            entrypoint = HostEntrypoint.Control;
            return true;
        }

        if (string.Equals(
            name?.Trim(),
            AgentCommandName,
            StringComparison.OrdinalIgnoreCase))
        {
            entrypoint = HostEntrypoint.Agent;
            return true;
        }

        entrypoint = HostEntrypoint.Agent;
        return false;
    }

    private static string? TryGetNativeCommandLine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        IntPtr commandLine = GetCommandLineW();
        return commandLine == IntPtr.Zero
            ? null
            : Marshal.PtrToStringUni(commandLine);
    }
}
