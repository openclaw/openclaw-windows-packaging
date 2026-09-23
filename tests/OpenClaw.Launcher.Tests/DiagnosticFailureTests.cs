using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests;

public sealed class DiagnosticFailureTests
{
    [Fact]
    public void BackendFailuresKeepTheirClassificationAndFailingOperation()
    {
        var exception = new MxcException(
            MxcErrorCode.BackendError,
            "No active session exists.",
            "backend_error",
            operation: "IsoSessionOps.RunProcessWithOptionsAsync");

        Assert.Equal(
            "MxcException: No active session exists. [code BackendError; " +
            "backend code backend_error; operation IsoSessionOps.RunProcessWithOptionsAsync]",
            DiagnosticFailure.Describe(exception));
    }

    // An unrecognized code is exactly the case where the verbatim backend
    // code is the only record of what the runtime said.
    [Fact]
    public void AnUnclassifiedBackendCodeIsKeptVerbatim()
    {
        var exception = new MxcException(
            MxcErrorCode.Unknown,
            "The backend is unavailable.",
            "backend_unavailable");

        Assert.Equal(
            "MxcException: The backend is unavailable. [code Unknown; backend code backend_unavailable]",
            DiagnosticFailure.Describe(exception));
    }

    [Fact]
    public void NativeFailuresKeepTheirWindowsErrorCode()
    {
        Assert.EndsWith(
            "[Win32 error 5]",
            DiagnosticFailure.Describe(new Win32Exception(5)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void IoFailuresKeepTheirHresult()
    {
        var exception = new IOException("The file is in use.", unchecked((int)0x80070020));

        Assert.Equal(
            "IOException: The file is in use. [HRESULT 0x80070020]",
            DiagnosticFailure.Describe(exception));
    }

    [Fact]
    public void RecordFaultsKeepTheirClassification()
    {
        var exception = new SessionStateException(
            SessionStateFault.Unreadable,
            "The session record could not be read.");

        Assert.Equal(
            "SessionStateException: The session record could not be read. [fault Unreadable]",
            DiagnosticFailure.Describe(exception));
    }

    // Wrapping for the user's benefit must not hide the cause from the log:
    // "the helper has not been staged" is actionable only with its reason.
    [Fact]
    public void EveryInnerCauseIsDescribedInOrder()
    {
        var exception = new SessionException(
            "The isolated-session helper has not been staged.",
            new SessionException(
                "The recorded shared workspace could not be opened safely.",
                new UnauthorizedAccessException("Access is denied.")));

        Assert.Equal(
            "SessionException: The isolated-session helper has not been staged. ---> " +
            "SessionException: The recorded shared workspace could not be opened safely. ---> " +
            "UnauthorizedAccessException: Access is denied. [HRESULT 0x80070005]",
            DiagnosticFailure.Describe(exception));
    }

    [Fact]
    public void AggregatedCausesAreNumberedRatherThanChained()
    {
        var exception = new SessionException(
            "Fresh setup local cleanup failed.",
            new AggregateException(
                new IOException("cleanup failed", unchecked((int)0x80070020)),
                new UnauthorizedAccessException("restore denied")));

        Assert.EndsWith(
            "---> 2 causes: (1) IOException: cleanup failed [HRESULT 0x80070020]; " +
            "(2) UnauthorizedAccessException: restore denied [HRESULT 0x80070005]",
            DiagnosticFailure.Describe(exception),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AMultiLineMessageStaysOnItsLogEntry()
    {
        string description = DiagnosticFailure.Describe(
            new InvalidOperationException("first line\r\nsecond line\n"));

        Assert.Equal("InvalidOperationException: first line second line", description);
    }

    [Fact]
    public void AnOverlyDeepChainIsBounded()
    {
        Exception exception = new InvalidOperationException("root");
        for (int index = 0; index < 20; index++)
        {
            exception = new InvalidOperationException($"wrapper {index}", exception);
        }

        string description = DiagnosticFailure.Describe(exception);

        Assert.EndsWith("---> ...", description, StringComparison.Ordinal);
        Assert.DoesNotContain("root", description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnhandledFailureKeepsTheThrowSiteOfEveryCause()
    {
        Exception exception;
        try
        {
            ThrowWrapped();
            throw new InvalidOperationException("unreachable");
        }
        catch (SessionException caught)
        {
            exception = caught;
        }

        string description = DiagnosticFailure.DescribeWithStackTrace(exception);
        string[] lines = description.Split(Environment.NewLine);

        Assert.StartsWith(
            "SessionException: outer ---> IOException: inner",
            lines[0],
            StringComparison.Ordinal);
        int innerFrame = Array.FindIndex(
            lines,
            line => line.Contains(nameof(ThrowInner), StringComparison.Ordinal));
        int boundary = Array.FindIndex(
            lines,
            line => line.Contains("End of inner exception stack trace", StringComparison.Ordinal));
        int outerFrame = Array.FindLastIndex(
            lines,
            line => line.Contains(nameof(ThrowWrapped), StringComparison.Ordinal));
        Assert.True(innerFrame > 0 && innerFrame < boundary && boundary < outerFrame, description);
    }

    [Fact]
    public void AFailureThatWasNeverThrownHasNoStackTraceToAdd()
    {
        var exception = new InvalidOperationException("constructed only");

        Assert.Equal(
            DiagnosticFailure.Describe(exception),
            DiagnosticFailure.DescribeWithStackTrace(exception));
    }

    // Fresh setup reports a cleanup failure and a restore failure together; the
    // second one's throw site must not be lost behind the first.
    [Fact]
    public void EveryAggregatedCauseKeepsItsThrowSite()
    {
        Exception exception = new SessionException(
            "Fresh setup local cleanup failed.",
            new AggregateException(Capture(ThrowCleanup), Capture(ThrowRestore)));

        string description = DiagnosticFailure.DescribeWithStackTrace(exception);

        Assert.Contains(nameof(ThrowCleanup), description, StringComparison.Ordinal);
        Assert.Contains(nameof(ThrowRestore), description, StringComparison.Ordinal);
    }

    private static IOException Capture(Action action)
    {
        try
        {
            action();
        }
        catch (IOException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The action did not fail.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowCleanup() => throw new IOException("cleanup failed");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowRestore() => throw new IOException("restore failed");

    // Kept out of line so both throw sites appear as their own frames.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWrapped()
    {
        try
        {
            ThrowInner();
        }
        catch (IOException exception)
        {
            throw new SessionException("outer", exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner() => throw new IOException("inner");
}
