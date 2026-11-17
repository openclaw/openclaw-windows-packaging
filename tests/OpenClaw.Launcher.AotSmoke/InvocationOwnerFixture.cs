using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace OpenClaw.Launcher.AotSmoke;

internal static partial class SmokeProgram
{
    private static int RunInvocationTreeFixture(string root, string role, string events)
    {
        File.WriteAllText(Path.Combine(root, role + ".pid"), Environment.ProcessId.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        if (role == "descendant")
        {
            using var ready = EventWaitHandle.OpenExisting(events + ".tree-ready");
            using var release = EventWaitHandle.OpenExisting(events + ".tree-release");
            ready.Set();
            release.WaitOne();
            File.WriteAllText(Path.Combine(root, "late-effect"), "descendant ran");
            return 0;
        }
        var start = new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            UseShellExecute = false
        };
        if (Path.GetFileName(start.FileName).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OpenClaw.Launcher.AotSmoke.dll"));
        }
        foreach (string argument in new[] { "--invocation-tree-fixture", root,
            role == "parent" ? "child" : "descendant", events })
        {
            start.ArgumentList.Add(argument);
        }
        using Process child = Process.Start(start)!;
        if (role == "parent")
        {
            using var exitParent = EventWaitHandle.OpenExisting(events + ".exit-parent");
            using var childExited = new EventWaitHandle(false, EventResetMode.ManualReset)
            {
                SafeWaitHandle = new SafeWaitHandle(child.SafeHandle.DangerousGetHandle(), ownsHandle: false)
            };
            if (WaitHandle.WaitAny([exitParent, childExited]) == 0)
            {
                return 37;
            }
        }
        child.WaitForExit();
        return child.ExitCode;
    }

    private static int RunInvocationOwnerFixture(string request, string readyName, string releaseName, string kind)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        using Launcher.WindowsInvocationJob job = Launcher.WindowsInvocationJob.Create(identity.User!.Value);
        using var ready = EventWaitHandle.OpenExisting(readyName);
        using var release = EventWaitHandle.OpenExisting(releaseName);
        string mode = kind == "state-transfer" ? job.StateTransferMode : job.Mode;
        File.WriteAllText(request + ".owner-mode", mode);
        if (kind == "launch-paused")
        {
            ready.Set();
            release.WaitOne();
        }
        using Process coordinator = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "openclaw-session-host.exe"),
            ArgumentList = { mode, request },
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(request)!,
            Environment =
            {
                ["OPENCLAW_SESSION_STATE_DIRECTORY"] =
                    Path.Combine(Path.GetDirectoryName(request)!, "state-access")
            }
        })!;
        job.MarkDispatched();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(request)!, "invocation-mode.txt"),
            Launcher.WindowsInvocationJob.ModePrefix + Launcher.WindowsInvocationJob.InvocationId(job.Mode));
        File.WriteAllText(request + ".coordinator", coordinator.Id.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        ready.Set();
        release.WaitOne();
        job.Stop();
        coordinator.WaitForExit();
        return coordinator.ExitCode;
    }
}
