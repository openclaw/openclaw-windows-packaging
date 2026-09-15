using OpenClaw.Launcher.Mxc;

namespace OpenClaw.Launcher.Tests.Mxc;

public sealed class MxcSandboxIdTests
{
    [Fact]
    public void IdentityPreservesTheCompleteOpaqueValue()
    {
        // The backend carries the provisioning app identity inside the id, so
        // persisting only the trailing instance segment loses it.
        const string value = "iso:PFN:Contoso.App_8wekyb3d8bbwe:5f2c";

        MxcSandboxId sandboxId = MxcSandboxId.Parse(value);

        Assert.Equal(value, sandboxId.Value);
        Assert.Equal(value, sandboxId.ToString());
        Assert.Equal("iso", sandboxId.BackendPrefix);
        Assert.True(sandboxId.IsIsolationSession);
    }

    [Fact]
    public void AnotherBackendsIdentityIsNotTreatedAsIsolationSession() =>
        Assert.False(MxcSandboxId.Parse("wslc:abc").IsIsolationSession);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc123")]
    [InlineData(":abc123")]
    public void IdentitiesWithoutABackendPrefixAreMalformed(string value)
    {
        MxcException exception =
            Assert.Throws<MxcException>(() => MxcSandboxId.Parse(value));

        Assert.Equal(MxcErrorCode.MalformedId, exception.Code);
    }
}
