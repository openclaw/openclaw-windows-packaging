using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using OpenClaw.SessionProtocol;

namespace OpenClaw.SessionHost;

/// <summary>
/// The <c>--inspect</c> mode: re-establishes whether a recorded gateway is
/// still ours and still serving.
/// </summary>
/// <remarks>
/// Every answer is an observation made now, inside the session. Nothing is
/// inferred from the recorded state itself, because a record survives the
/// process it describes: stopping the sandbox terminates detached work without
/// notifying anything.
/// </remarks>
internal static class SessionInspector
{
    public static int Run(
        string requestPath,
        Func<string, string> readFile,
        Action<string, string> writeFile)
    {
        string resultPath = SessionLaunchProtocol.ResultPathFor(requestPath);
        SessionInspectRequest request;

        try
        {
            request = SessionInspectProtocol.ReadRequest(readFile(requestPath));
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            Write(
                writeFile,
                resultPath,
                new SessionInspectResult { Error = exception.Message });
            return SessionLaunchProtocol.HelperFailureExitCode;
        }

        Write(writeFile, resultPath, Inspect(request, readFile));
        return 0;
    }

    internal static SessionInspectResult Inspect(
        SessionInspectRequest request,
        Func<string, string> readFile,
        Func<IReadOnlyDictionary<int, ulong>>? captureSequences = null,
        Action<Process>? probeAccess = null,
        Func<IReadOnlyDictionary<int, DateTimeOffset>>? captureCreationTimes = null)
    {
        if (request.LaunchPending)
        {
            // No verified PID was recorded before the interrupted launch, so
            // this reconciliation must never select or act on a process. The
            // caller must retain the launch intent and use owned teardown for
            // recovery rather than treating the absence of a PID as proof
            // that no process exists.
            return new SessionInspectResult
            {
                RequestId = request.RequestId,
                Error = "The gateway launch was not confirmed; process ownership cannot be established."
            };
        }

        try
        {
            using Process process = Process.GetProcessById(request.ProcessId);
            probeAccess?.Invoke(process);
            // Pin the identity for the complete observation. Short-lived query handles
            // could allow the root PID to be reused between start-time and image checks.
            using SafeProcessHandle? query = OperatingSystem.IsWindows()
                ? SessionProcessQuery.Open(request.ProcessId)
                : null;
            if (query is null)
            {
                _ = process.Handle;
            }
            if (query is not null ? SessionProcessQuery.HasExited(query) : process.HasExited)
            {
                return NotFound(request, readFile);
            }
            bool matches = (query is not null
                ? SessionProcessQuery.StartTime(query)
                : process.StartTime.ToUniversalTime()) == request.ProcessStartTimeUtc;
            string? error = matches
                ? ValidateImage(query is not null
                    ? SessionProcessQuery.ImagePath(query)
                    : process.MainModule?.FileName, request)
                : null;
            SessionSupervisorStatus? supervisor = ReadSupervisorStatus(request.StatusPath, readFile);

            // Ownership is established by finding a listener belonging to this
            // process tree, not by confirming a port the host supplied. When
            // the user pinned a port, it is additionally checked against what
            // was observed, so a gateway on the wrong port is not reported as
            // healthy.
            IReadOnlyDictionary<int, ulong> before = matches && error is null
                ? CaptureSequences(captureSequences ?? WindowsProcessSnapshot.Capture)
                : new Dictionary<int, ulong>();
            IReadOnlyList<(int Port, int Owner)> listeners = matches && error is null
                ? TcpListenerOwnership.GetListeners()
                : [];
            ProcessTreeSnapshot tree = new();
            IReadOnlyList<int> owned = GuestProcessObserver.ListeningPortsOwnedBy(
                listeners, tree, request.ProcessId);
            List<SessionOwnedListener> identities = ObserveOwnedListeners(listeners, tree, request.ProcessId);
            IReadOnlyDictionary<int, ulong> after = identities.Count > 0
                ? CaptureSequences(captureSequences ?? WindowsProcessSnapshot.Capture)
                : new Dictionary<int, ulong>();
            if (identities.Any(identity =>
                !tree.HasStableAncestry(identity.ProcessId, request.ProcessId, before, after)))
            {
                identities.Clear();
            }
            identities = [.. identities.Select(identity =>
                identity with { SequenceNumber = after[identity.ProcessId] })];
            bool ownsConfigured = request.Port is not int configured ||
                owned.Contains(configured);

            return new SessionInspectResult
            {
                RequestId = request.RequestId,
                ProcessFound = true,
                StartTimeMatches = matches,
                SupervisorState = supervisor?.State,
                SupervisorDetail = supervisor?.Detail,
                PortListening = request.Port is int probe
                    ? GuestProcessObserver.AnythingListeningOn(probe)
                    : owned.Count > 0,
                ListeningPorts = owned,
                OwnedListeners = identities.Count > 0 ? identities : null,
                ListenerOwned = owned.Count > 0 && ownsConfigured,
                Error = error
            };
        }
        catch (ArgumentException)
        {
            return NotFound(request, readFile);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return ReconcileAccessDenied(request, exception, captureCreationTimes);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or NotSupportedException or InvalidDataException)
        {
            return new SessionInspectResult { RequestId = request.RequestId, Error = exception.Message };
        }
    }

    internal static SessionInspectResult ReconcileAccessDenied(
        SessionInspectRequest request,
        Win32Exception denial,
        Func<IReadOnlyDictionary<int, DateTimeOffset>>? captureCreationTimes)
    {
        try
        {
            var times = (captureCreationTimes ?? WindowsProcessSnapshot.CaptureCreationTimes)();
            bool found = times.TryGetValue(request.ProcessId, out DateTimeOffset created);
            if (!found || created != request.ProcessStartTimeUtc)
            {
                return new SessionInspectResult
                {
                    RequestId = request.RequestId,
                    ProcessFound = found,
                    StartTimeMatches = false
                };
            }
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or
            NotSupportedException or ArgumentOutOfRangeException or Win32Exception)
        {
            // Unavailable identity evidence must retain the record and block replacement.
        }
        return new SessionInspectResult { RequestId = request.RequestId, Error = denial.Message };
    }

    internal static List<SessionOwnedListener> ObserveOwnedListeners(
        IReadOnlyList<(int Port, int Owner)> listeners, ProcessTreeSnapshot tree, int ancestor)
    {
        List<SessionOwnedListener> identities = [];
        foreach ((int port, int owner) in listeners.Distinct())
        {
            if (!tree.IsSelfOrDescendant(owner, ancestor))
            {
                continue;
            }
            DateTimeOffset? startTime = tree.StartTimeOf(owner);
            if (startTime is null)
            {
                return [];
            }
            identities.Add(new SessionOwnedListener
            {
                Port = port,
                ProcessId = owner,
                ProcessStartTimeUtc = startTime.Value
            });
        }
        return identities;
    }

    private static IReadOnlyDictionary<int, ulong> CaptureSequences(
        Func<IReadOnlyDictionary<int, ulong>> captureSequences)
    {
        try
        {
            return captureSequences();
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            return new Dictionary<int, ulong>();
        }
    }

    internal static string? ValidateImage(Process process, SessionInspectRequest request) =>
        ValidateImage(process.MainModule?.FileName, request);

    private static string? ValidateImage(string? imagePath, SessionInspectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.HelperPath))
        {
            return "The recorded helper image is missing; ownership cannot be verified.";
        }
        return string.Equals(imagePath, request.HelperPath, StringComparison.OrdinalIgnoreCase)
            ? null : "The process image differs from the recorded gateway helper.";
    }

    private static SessionInspectResult NotFound(
        SessionInspectRequest request,
        Func<string, string> readFile)
    {
        SessionSupervisorStatus? supervisor = ReadSupervisorStatus(request.StatusPath, readFile);
        return new SessionInspectResult
        {
            RequestId = request.RequestId,
            SupervisorState = supervisor?.State,
            SupervisorDetail = supervisor?.Detail
        };
    }

    private static SessionSupervisorStatus? ReadSupervisorStatus(
        string? statusPath,
        Func<string, string> readFile)
    {
        if (string.IsNullOrWhiteSpace(statusPath))
        {
            return null;
        }

        try
        {
            return SessionInspectProtocol.ReadStatus(readFile(statusPath));
        }
        catch (Exception exception) when (
            exception is SessionLaunchException or IOException or
            UnauthorizedAccessException)
        {
            // A missing or unreadable status file is reported as no state,
            // never as a failed inspection: the process evidence stands on its
            // own.
            return null;
        }
    }

    private static void Write(
        Action<string, string> writeFile,
        string resultPath,
        SessionInspectResult result)
    {
        try
        {
            writeFile(resultPath, SessionInspectProtocol.SerializeResult(result));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // The host reports a missing result as an unanswered inspection.
        }
    }
}
