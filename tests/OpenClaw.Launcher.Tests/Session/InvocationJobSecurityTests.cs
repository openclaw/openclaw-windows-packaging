using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class InvocationJobSecurityTests
{
    private const string Owner = "S-1-5-21-1-2-3-1000";
    private const string Agent = "S-1-5-21-1-2-3-1001";
    private const byte MandatoryLabelAceType = 0x11;
    private const int NoWriteUp = 0x1;

    // From an elevated terminal the unlabeled job inherited the launcher's High
    // integrity, and the Medium-integrity agent's OpenJobObject(ASSIGN_PROCESS)
    // failed with "Unable to open the invocation job" despite the DACL grant.
    [Theory]
    [InlineData(0x0005u)]
    [InlineData(0x00100000u)]
    [InlineData(0x00000002u)]
    public void InvocationObjectsCarryAnExplicitMediumNoWriteUpLabel(uint agentAccess)
    {
        var descriptor = new RawSecurityDescriptor(
            WindowsInvocationJob.AccessDescriptor(Owner, Agent, agentAccess));

        GenericAce label = Assert.Single(descriptor.SystemAcl!.Cast<GenericAce>());
        var custom = Assert.IsType<CustomAce>(label);
        Assert.Equal(MandatoryLabelAceType, (byte)custom.AceType);
        byte[] body = custom.GetOpaque()!;
        Assert.Equal(NoWriteUp, BitConverter.ToInt32(body, 0));
        Assert.Equal(
            new SecurityIdentifier(WellKnownSidType.WinMediumLabelSid, null),
            new SecurityIdentifier(body, 4));
    }

    [Fact]
    public void TheLabelLeavesTheProtectedOwnerAndAgentGrantsUnchanged()
    {
        var descriptor = new RawSecurityDescriptor(
            WindowsInvocationJob.AccessDescriptor(Owner, Agent, 0x0005));

        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
        Assert.Collection(
            descriptor.DiscretionaryAcl!.Cast<CommonAce>(),
            owner =>
            {
                Assert.Equal(new SecurityIdentifier(Owner), owner.SecurityIdentifier);
                Assert.Equal(unchecked((int)0x10000000), owner.AccessMask);
            },
            agent =>
            {
                Assert.Equal(new SecurityIdentifier(Agent), agent.SecurityIdentifier);
                Assert.Equal(0x0005, agent.AccessMask);
            });
    }
}
