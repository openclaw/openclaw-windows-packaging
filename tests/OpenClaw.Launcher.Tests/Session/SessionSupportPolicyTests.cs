using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionSupportPolicyTests
{
    private const string PackageFamilyName = "OpenClaw.Gateway_abc123";

    private static MxcReadinessReport Ready(
        bool? backendAvailable = true,
        string? runtimeUnavailableReason = null,
        MxcHostSupport hostSupport = MxcHostSupport.Supported,
        string? probeFailureReason = null) =>
        new(
            runtimeUnavailableReason is null ? @"C:\Package\mxc\x64" : null,
            null,
            runtimeUnavailableReason,
            hostSupport,
            null,
            backendAvailable is null
                ? MxcSupportEvidence.HostBuild
                : MxcSupportEvidence.BackendProbe,
            backendAvailable is null
                ? null
                : new MxcBackendProbe(backendAvailable.Value, "base-container", []),
            probeFailureReason);

    [Fact]
    public void SupportedMachineIsAccepted() =>
        SessionSupportPolicy.EnsureSupported(PackageFamilyName, Ready());

    [Fact]
    public void ProbeOverridesAnUnsupportedBuildVerdict() =>
        SessionSupportPolicy.EnsureSupported(
            PackageFamilyName,
            Ready(backendAvailable: true, hostSupport: MxcHostSupport.Unsupported));

    [Fact]
    public void UndeterminableSupportIsAttemptedRatherThanRefused()
    {
        // Nothing was measured, so refusing would deny a machine that may well
        // work. The backend's own error is the honest verdict.
        SessionSupportPolicy.EnsureSupported(
            PackageFamilyName,
            Ready(
                backendAvailable: null,
                hostSupport: MxcHostSupport.Unknown,
                probeFailureReason: "the executor crashed"));
    }

    [Fact]
    public void ProbeSayingUnavailableFailsWithRemediation()
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionSupportPolicy.EnsureSupported(
                PackageFamilyName,
                Ready(backendAvailable: false)));

        Assert.Contains("not available", exception.Message, StringComparison.Ordinal);
        AssertRecommendsNewerWindows(exception);
    }

    [Fact]
    public void UnsupportedBuildWithoutAProbeFails()
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionSupportPolicy.EnsureSupported(
                PackageFamilyName,
                Ready(backendAvailable: null, hostSupport: MxcHostSupport.Unsupported)));

        Assert.Contains(
            "does not support isolated agent sessions",
            exception.Message,
            StringComparison.Ordinal);
        AssertRecommendsNewerWindows(exception);
    }

    [Fact]
    public void ProbeFailureReasonIsCarriedIntoTheExplanation()
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionSupportPolicy.EnsureSupported(
                PackageFamilyName,
                Ready(
                    backendAvailable: null,
                    hostSupport: MxcHostSupport.Unsupported,
                    probeFailureReason: "the executor crashed")));

        Assert.Contains("the executor crashed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingRuntimeFails()
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionSupportPolicy.EnsureSupported(
                PackageFamilyName,
                Ready(runtimeUnavailableReason: "the runtime directory is absent")));

        Assert.Contains(
            "the runtime directory is absent",
            exception.Message,
            StringComparison.Ordinal);
        AssertRecommendsNewerWindows(exception);
    }

    [Fact]
    public void UnpackagedBuildFails()
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionSupportPolicy.EnsureSupported(null, Ready()));

        Assert.Contains("installed package", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertRecommendsNewerWindows(SessionException exception)
    {
        Assert.Contains(
            "newer version of Windows",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Windows Update", exception.Message, StringComparison.Ordinal);
        Assert.Contains("#requirements", exception.Message, StringComparison.Ordinal);
    }
}
