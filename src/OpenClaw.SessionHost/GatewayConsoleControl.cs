using System.Runtime.InteropServices;

namespace OpenClaw.SessionHost;

/// <summary>
/// Delivers a stop request to a supervised gateway the way Windows delivers Ctrl+C: Node.js raises
/// SIGINT, which OpenClaw handles as a graceful stop that releases its state leases before exiting.
/// </summary>
/// <remarks>
/// The supervisor starts the gateway without a window, which gives the gateway a console of its own
/// that only its process tree shares, so a Ctrl+C on that console reaches nothing else. Sending one
/// moves the caller onto that console and makes it ignore Ctrl+C for the rest of its life, because
/// delivery is asynchronous. Only the short-lived <c>--stop</c> helper may call
/// <see cref="TryInterrupt"/>.
/// </remarks>
internal static class GatewayConsoleControl
{
    private const uint CtrlCEvent = 0;

    /// <summary>Sends Ctrl+C to the console the first attachable process in the list uses.</summary>
    /// <param name="consoleMembers">Live gateway job members, each pinned by the caller so its ID cannot be reused.</param>
    /// <returns>Whether the event was generated; false leaves the caller to terminate the tree.</returns>
    public static bool TryInterrupt(IReadOnlyList<int> consoleMembers)
    {
        ArgumentNullException.ThrowIfNull(consoleMembers);
        _ = FreeConsole();
        foreach (int processId in consoleMembers)
        {
            if (!AttachConsole((uint)processId))
            {
                continue;
            }
            try
            {
                // Group 0 is every process on the console: the gateway and its descendants, and this
                // helper, which must therefore ignore the event before generating it.
                return SetConsoleCtrlHandler(IntPtr.Zero, add: true) &&
                    GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            }
            finally
            {
                _ = FreeConsole();
            }
        }
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
