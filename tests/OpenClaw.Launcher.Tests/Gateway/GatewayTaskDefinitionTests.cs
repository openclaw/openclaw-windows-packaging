using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class GatewayTaskIdentityTests
{
    [Fact]
    public void TaskNameIsScopedByPackageAndUser()
    {
        GatewayTaskIdentity first = GatewayTaskIdentity.Create("Public_abc", "S-1-5-21-1");
        GatewayTaskIdentity second = GatewayTaskIdentity.Create("Public_abc", "S-1-5-21-2");
        GatewayTaskIdentity other = GatewayTaskIdentity.Create("Internal_abc", "S-1-5-21-1");

        Assert.NotEqual(first.Name, second.Name);
        Assert.NotEqual(first.Name, other.Name);
    }

    [Theory]
    [InlineData("Public\\abc", "S-1-5-21-1")]
    [InlineData("Public_abc", "DOMAIN\\user")]
    public void ABackslashInANameComponentIsRejected(string packageFamilyName, string sid)
    {
        // Task Scheduler reads a backslash as a folder separator, so such a
        // name would silently address a different folder.
        Assert.Throws<ArgumentException>(
            () => GatewayTaskIdentity.Create(packageFamilyName, sid));
    }

    [Fact]
    public void OnlyThisPackagesTasksAreRecognized()
    {
        GatewayTaskIdentity identity =
            GatewayTaskIdentity.Create("Public_abc", "S-1-5-21-1");

        Assert.True(GatewayTaskIdentity.BelongsToPackage(identity.Name, "Public_abc"));
        Assert.False(GatewayTaskIdentity.BelongsToPackage(identity.Name, "Internal_abc"));
    }
}

public sealed class GatewayTaskDefinitionTests
{
    private static GatewayTaskSnapshot Desired() =>
        GatewayTaskDefinition.CreateSnapshot(
            "S-1-5-21-1",
            @"C:\Windows\System32\cmd.exe",
            @"C:\state\gateway-launcher.cmd");

    [Fact]
    public void TheGeneratedDefinitionRoundTrips()
    {
        GatewayTaskSnapshot desired = Desired();
        string xml = GatewayTaskDefinition.CreateXml(desired, "OpenClaw Gateway test");

        Assert.True(GatewayTaskDefinition.TryParse(
            xml,
            out GatewayTaskSnapshot? parsed,
            out string? detail));
        Assert.Null(detail);
        Assert.Equal(desired, parsed);
    }

    [Fact]
    public void TheGatewayIsNotGatedOnPowerOrATimeLimit()
    {
        GatewayTaskSnapshot desired = Desired();

        Assert.False(desired.DisallowStartIfOnBatteries);
        Assert.False(desired.StopIfGoingOnBatteries);
        Assert.Equal(GatewayTaskDefinition.NoExecutionTimeLimit, desired.ExecutionTimeLimit);
    }

    [Fact]
    public void ASecondSignInDoesNotStartASecondGateway()
    {
        Assert.Equal(GatewayTaskDefinition.IgnoreNewInstances, Desired().MultipleInstancesPolicy);
    }

    [Fact]
    public void TheTaskRunsUnelevatedAsTheSignedInUser()
    {
        GatewayTaskSnapshot desired = Desired();

        Assert.Equal("LeastPrivilege", desired.RunLevel);
        Assert.Equal("InteractiveToken", desired.LogonType);
        Assert.Equal("S-1-5-21-1", desired.UserId);
        Assert.Equal("S-1-5-21-1", desired.LogonTriggerUserId);
    }

    [Fact]
    public void TheActionRunsTheLauncherThroughTheCommandProcessor()
    {
        GatewayTaskSnapshot desired = Desired();

        Assert.Equal(@"C:\Windows\System32\cmd.exe", desired.Command);
        Assert.Contains(@"C:\state\gateway-launcher.cmd", desired.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableXmlIsReportedRatherThanGuessed()
    {
        Assert.False(GatewayTaskDefinition.TryParse(
            "<Task><not-closed>",
            out GatewayTaskSnapshot? parsed,
            out string? detail));
        Assert.Null(parsed);
        Assert.NotNull(detail);
    }

    [Fact]
    public void AbsentSettingsParseToTaskSchedulersOwnDefaults()
    {
        const string xml = """
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers />
              <Principals />
              <Settings />
              <Actions />
            </Task>
            """;

        Assert.True(GatewayTaskDefinition.TryParse(xml, out GatewayTaskSnapshot? parsed, out _));
        Assert.NotNull(parsed);

        // A definition that omits these is not equivalent to ours, so drift
        // must not be hidden by optimistic defaults.
        Assert.True(parsed.DisallowStartIfOnBatteries);
        Assert.True(parsed.StopIfGoingOnBatteries);
        Assert.Equal("PT72H", parsed.ExecutionTimeLimit);
        Assert.False(parsed.HasSingleLogonTrigger);
        Assert.False(parsed.HasSingleExecAction);
    }
}
