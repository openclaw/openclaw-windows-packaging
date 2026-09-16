using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenClaw.SessionHost;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

/// <summary>
/// Exercises the guest-side ownership evidence against real processes and real
/// sockets. Fakes cannot cover this: the whole point of the interop is to
/// answer questions about the operating system's actual state.
/// </summary>
public sealed class GuestProcessObserverTests
{
    [Fact]
    public void AListenerIsAttributedToTheProcessThatOpenedIt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Assert.True(GuestProcessObserver.AnythingListeningOn(port));
            Assert.True(GuestProcessObserver.OwnsListenerOn(port, Environment.ProcessId));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void AListenerIsNotCreditedToAnUnrelatedProcess()
    {
        // An open port proves only that something is listening. Without this
        // distinction any program in the session could be mistaken for the
        // gateway.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using Process unrelated = StartLongRunningProcess();
            try
            {
                Assert.True(GuestProcessObserver.AnythingListeningOn(port));
                Assert.False(GuestProcessObserver.OwnsListenerOn(port, unrelated.Id));
            }
            finally
            {
                Kill(unrelated);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void AClosedPortHasNoListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.False(GuestProcessObserver.AnythingListeningOn(port));
    }

    [Fact]
    public void AChildProcessCountsAsOwnedByItsParent()
    {
        // The gateway is a child of the supervisor, not the supervisor itself,
        // so an ownership check comparing identifiers alone would reject every
        // real gateway.
        using Process child = StartLongRunningProcess();
        try
        {
            Assert.True(ProcessAncestry.IsSelfOrDescendant(child.Id, Environment.ProcessId));
            Assert.True(ProcessAncestry.IsSelfOrDescendant(
                Environment.ProcessId,
                Environment.ProcessId));
            Assert.False(ProcessAncestry.IsSelfOrDescendant(Environment.ProcessId, child.Id));
        }
        finally
        {
            Kill(child);
        }
    }

    [Fact]
    public void ALiveProcessReportsItsCreationTime()
    {
        using Process child = StartLongRunningProcess();
        try
        {
            DateTimeOffset? observed = GuestProcessObserver.GetStartTimeUtc(child.Id);

            Assert.NotNull(observed);
            Assert.Equal(
                child.StartTime.ToUniversalTime(),
                observed.Value,
                TimeSpan.FromSeconds(1));
        }
        finally
        {
            Kill(child);
        }
    }

    [Fact]
    public void AnExitedProcessHasNoCreationTime()
    {
        using Process child = StartLongRunningProcess();
        Kill(child);
        child.WaitForExit();

        Assert.Null(GuestProcessObserver.GetStartTimeUtc(child.Id));
    }

    internal static Process StartLongRunningProcess()
    {
        // An inbox executable that simply waits, so the test needs no fixture
        // binary of its own.
        ProcessStartInfo startInfo = new()
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("echo OPENCLAW-CHILD-READY & set /p hold=");

        Process process = Process.Start(startInfo)!;
        string? ready = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter().GetResult();
        if (ready?.Trim() != "OPENCLAW-CHILD-READY")
        {
            Kill(process);
            process.Dispose();
            throw new InvalidOperationException("The fixture process did not signal readiness.");
        }
        return process;
    }

    internal static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }
}

public sealed class SessionInspectorTests
{
    [Fact]
    public void EvenOneTickMismatchCannotAuthorizeStop()
    {
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            SessionInspectRequest request = RequestFor(child) with
            {
                ProcessStartTimeUtc = child.StartTime.ToUniversalTime().AddTicks(1)
            };
            SessionInspectResult result = SessionTerminator.Stop(request, _ => throw new FileNotFoundException());
            Assert.False(result.StartTimeMatches);
            Assert.False(child.HasExited);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
        }
    }

    [Fact]
    public void MatchingPidAndTimeWithWrongImageCannotAuthorizeStop()
    {
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            SessionInspectResult result = SessionTerminator.Stop(
                RequestFor(child) with { HelperPath = @"C:\not-the-owned-helper.exe" },
                _ => throw new FileNotFoundException());
            Assert.NotNull(result.Error);
            Assert.False(child.HasExited);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
        }
    }

    private static SessionInspectRequest RequestFor(
        Process process,
        DateTimeOffset? startTimeOverride = null,
        int port = 0,
        string? statusPath = null) =>
        new()
        {
            RequestId = "r1",
            ProcessId = process.Id,
            HelperPath = process.StartInfo.FileName,
            ProcessStartTimeUtc =
                startTimeOverride ?? process.StartTime.ToUniversalTime(),
            Port = port,
            StatusPath = statusPath
        };

    [Fact]
    public void ALiveRecordedProcessIsRecognized()
    {
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            SessionInspectResult result = SessionInspector.Inspect(
                RequestFor(child),
                _ => throw new FileNotFoundException());

            Assert.True(result.ProcessFound);
            Assert.True(result.StartTimeMatches);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
        }
    }

    [Fact]
    public void AReusedIdentifierIsNotAcceptedAsTheRecordedProcess()
    {
        // Windows reuses process identifiers, so a live process with the
        // recorded identifier but a different creation time is someone else's.
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            SessionInspectResult result = SessionInspector.Inspect(
                RequestFor(child, startTimeOverride: DateTimeOffset.UnixEpoch),
                _ => throw new FileNotFoundException());

            Assert.True(result.ProcessFound);
            Assert.False(result.StartTimeMatches);
            Assert.False(result.IsOwnedAndHealthy);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
        }
    }

    [Fact]
    public void AListenerIsNotCreditedToAProcessThatFailedTheIdentityCheck()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            SessionInspectResult result = SessionInspector.Inspect(
                RequestFor(child, startTimeOverride: DateTimeOffset.UnixEpoch, port: port),
                _ => throw new FileNotFoundException());

            Assert.True(result.PortListening);
            Assert.False(result.ListenerOwned);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
            listener.Stop();
        }
    }

    [Fact]
    public void AnExitedProcessReportsItsLastSupervisorObservation()
    {
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        GuestProcessObserverTests.Kill(child);
        child.WaitForExit();

        SessionInspectResult result = SessionInspector.Inspect(
            RequestFor(
                child,
                startTimeOverride: DateTimeOffset.UnixEpoch,
                statusPath: "status.json"),
            _ => SessionInspectProtocol.SerializeStatus(new SessionSupervisorStatus
            {
                State = SessionSupervisorStatus.ExitedState,
                Detail = "the application exited with code 78"
            }));

        Assert.False(result.ProcessFound);
        Assert.False(result.IsOwnedAndHealthy);
        Assert.Equal(SessionSupervisorStatus.ExitedState, result.SupervisorState);
        Assert.Equal("the application exited with code 78", result.SupervisorDetail);
    }

    [Fact]
    public void AnUnreadableStatusFileDoesNotFailTheInspection()
    {
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            SessionInspectResult result = SessionInspector.Inspect(
                RequestFor(child, statusPath: @"C:\missing\status.json"),
                _ => throw new FileNotFoundException());

            Assert.Null(result.Error);
            Assert.Null(result.SupervisorState);
            Assert.True(result.ProcessFound);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
        }
    }

    [Fact]
    public void TheSupervisorStateIsReportedWhenItIsReadable()
    {
        using Process child = GuestProcessObserverTests.StartLongRunningProcess();
        try
        {
            SessionInspectResult result = SessionInspector.Inspect(
                RequestFor(child, statusPath: "status.json"),
                _ => SessionInspectProtocol.SerializeStatus(new SessionSupervisorStatus
                {
                    State = SessionSupervisorStatus.RunningState,
                    ProcessId = child.Id,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                }));

            Assert.Equal(SessionSupervisorStatus.RunningState, result.SupervisorState);
        }
        finally
        {
            GuestProcessObserverTests.Kill(child);
        }
    }

    [Fact]
    public void HealthRequiresEveryPieceOfEvidence()
    {
        var healthy = new SessionInspectResult
        {
            ProcessFound = true,
            StartTimeMatches = true,
            PortListening = true,
            ListenerOwned = true
        };

        Assert.True(healthy.IsOwnedAndHealthy);
        Assert.False((healthy with { ProcessFound = false }).IsOwnedAndHealthy);
        Assert.False((healthy with { StartTimeMatches = false }).IsOwnedAndHealthy);
        Assert.False((healthy with { PortListening = false }).IsOwnedAndHealthy);
        Assert.False((healthy with { ListenerOwned = false }).IsOwnedAndHealthy);
        Assert.False((healthy with { Error = "unreadable" }).IsOwnedAndHealthy);
    }
}
