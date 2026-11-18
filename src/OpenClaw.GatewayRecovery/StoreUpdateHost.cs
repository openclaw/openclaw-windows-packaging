using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace OpenClaw.GatewayRecovery;

/// <summary>
/// Opt-in prototype using the existing packaged, signed-in-user WinExe. It
/// observes Windows servicing, not Store polling or an automatic-update policy.
/// </summary>
internal static class StoreUpdateHost
{
    private const uint QueryEndSession = 0x0011;
    private const uint EndSession = 0x0016;
    private const uint Close = 0x0010;
    private const nint CloseApp = 1;
    private const int WindowProcedureIndex = -4;
    private const string Arguments = "--store-update-host";

    public static int Run(GatewayRecoveryEnvironment environment, Action<string> log)
    {
        if (environment.PackageFamilyName is null ||
            !PackageExecutionAlias.TryResolve(environment.PackageDirectory,
                environment.PackageFamilyName, GatewayRecoveryStart.ControlApplicationId,
                GatewayRecoveryStart.ControlExecutable, environment.LocalApplicationData,
                out string? controlPath, out _) || !File.Exists(controlPath))
        {
            log("Store update example requires this user's installed package and control alias.");
            return 1;
        }

        using var singleton = new Mutex(false,
            $"Local\\OpenClaw.StoreUpdateExample.{environment.PackageFamilyName}");
        bool acquired;
        try
        {
            acquired = singleton.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }
        if (!acquired)
        {
            log("Store update example is already running for this package and session.");
            return 1;
        }
        try
        {
            var session = new StoreUpdateSession(
                arguments => RunControl(controlPath, arguments, environment.WorkingDirectory, log),
                () => Marshal.ThrowExceptionForHR(Native.RegisterApplicationRestart(Arguments, 3)),
                () => Marshal.ThrowExceptionForHR(Native.UnregisterApplicationRestart()));
            if (!session.Start())
            {
                log("Store update example could not observe a running Gateway with a launch receipt. Run clawctl gateway-service status.");
                return 1;
            }
            return RunWindow(session, log);
        }
        finally
        {
            singleton.ReleaseMutex();
        }
    }

    private static string? RunControl(string path, string arguments, string directory, Action<string> log)
    {
        using Process process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        }) ?? throw new InvalidOperationException("The package control command did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        // Leave room in Windows' servicing deadline. Never wait indefinitely
        // inside WM_ENDSESSION or log captured Gateway output/credentials.
        int timeout = arguments.StartsWith("gateway-service start", StringComparison.Ordinal) ? 120_000 : 20_000;
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            log("Store update example: control command timed out; Gateway state is unverified.");
            return null;
        }
        _ = error.GetAwaiter().GetResult();
        string json = output.GetAwaiter().GetResult();
        log($"Store update example: control command exited with code {process.ExitCode}.");
        return process.ExitCode == 0 ? json : null;
    }

    private static int RunWindow(StoreUpdateSession session, Action<string> log)
    {
        // A hidden top-level window receives shutdown messages. HWND_MESSAGE
        // would not participate in the desktop shutdown broadcast.
        nint window = Native.CreateWindowExW(0, "STATIC", "OpenClaw Store update example",
            0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (window == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        nint previous = 0;
        bool servicingExit = false;
        Native.WindowProcedure procedure = Dispatch;
        try
        {
            previous = Native.SetWindowLongPtrW(window, WindowProcedureIndex,
                Marshal.GetFunctionPointerForDelegate(procedure));
            if (previous == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            log("Store update example is listening for Windows servicing shutdown.");
            int status;
            Native.Message message;
            while ((status = Native.GetMessageW(out message, 0, 0, 0)) > 0)
            {
                _ = Native.TranslateMessage(in message);
                Native.DispatchMessageW(in message);
            }
            return status < 0 ? 1 : unchecked((int)message.WParam);
        }
        finally
        {
            if (!servicingExit)
            {
                session.CancelRestart();
            }
            if (previous != 0)
            {
                Native.SetWindowLongPtrW(window, WindowProcedureIndex, previous);
            }
            if (Native.DestroyWindow(window) == 0)
            {
                log("Store update example could not destroy its notification window.");
            }
            GC.KeepAlive(procedure);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Exceptions cannot unwind through the native window procedure. Fail closed and record the exception type only.")]
        nint Dispatch(nint handle, uint message, nuint wParam, nint lParam)
        {
            try
            {
                bool servicing = (lParam & CloseApp) != 0;
                switch (message)
                {
                    case QueryEndSession:
                        return session.QueryEndSession(servicing) ? 1 : 0;
                    case EndSession:
                        session.EndSession(wParam != 0, servicing);
                        if (wParam != 0)
                        {
                            servicingExit = servicing && session.RestartRegistered;
                            log(servicingExit
                                ? "Gateway stopped for servicing; Windows restart is registered."
                                : "Servicing restart not armed; no verified Gateway stop.");
                            Native.PostQuitMessage(0);
                        }
                        return 0;
                    case Close:
                        session.CancelRestart();
                        Native.PostQuitMessage(0);
                        return 0;
                    default:
                        return Native.CallWindowProcW(previous, handle, message, wParam, lParam);
                }
            }
            catch (Exception exception)
            {
                // Native unregister is nonthrowing here, including if the
                // original failure came from registration itself.
                _ = Native.UnregisterApplicationRestart();
                log($"Store update example failed: {exception.GetType().Name}.");
                Native.PostQuitMessage(1);
                return 0;
            }
        }
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            public nint Window;
            public uint Id;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
            public uint Private;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern int RegisterApplicationRestart(string arguments, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true)]
        internal static extern int UnregisterApplicationRestart();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern nint CreateWindowExW(uint extendedStyle, string className,
            string title, uint style, int x, int y, int width, int height,
            nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern nint SetWindowLongPtrW(nint window, int index, nint value);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint CallWindowProcW(nint previous, nint window, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern int GetMessageW(out Message message, nint window, uint min, uint max);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int TranslateMessage(in Message message);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern nint DispatchMessageW(in Message message);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", ExactSpelling = true)]
        internal static extern int DestroyWindow(nint window);
    }
}
