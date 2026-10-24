using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

public sealed class WindowsScriptHostPolicyTests
{
    private const string MachineEnabled =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Script Host\Settings\Enabled";

    private const string UserEnabled =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows Script Host\Settings\Enabled";

    [Fact]
    public void AbsentSettingsLeaveWindowsScriptHostEnabled()
    {
        Assert.Null(WindowsScriptHostPolicy.FindDisablingSetting(new FakeWindowsScriptHostSettings()));
    }

    [Fact]
    public void ExplicitlyEnabledSettingsLeaveWindowsScriptHostEnabled()
    {
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.Machine, "Enabled", 1);
        settings.Set(WindowsScriptHostSettingsScope.User, "Enabled", 1);

        Assert.Null(WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }

    [Fact]
    public void AMachineWideZeroDisablesWindowsScriptHost()
    {
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.Machine, "Enabled", 0);

        Assert.Equal(MachineEnabled, WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }

    [Fact]
    public void AUserZeroDisablesWindowsScriptHost()
    {
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.User, "Enabled", 0);

        Assert.Equal(UserEnabled, WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MachineIgnoreUserSettingsMakesAUserZeroIrrelevant(int ignoreUserSettings)
    {
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.Machine, "IgnoreUserSettings", ignoreUserSettings);
        settings.Set(WindowsScriptHostSettingsScope.User, "Enabled", 0);

        Assert.Null(WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }

    [Fact]
    public void MachineIgnoreUserSettingsOfZeroStillHonorsAUserZero()
    {
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.Machine, "IgnoreUserSettings", 0);
        settings.Set(WindowsScriptHostSettingsScope.User, "Enabled", 0);

        Assert.Equal(UserEnabled, WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }

    [Fact]
    public void IgnoreUserSettingsOnlyCountsUnderTheMachineScope()
    {
        // A user-scope IgnoreUserSettings cannot excuse the user's own zero.
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.User, "IgnoreUserSettings", 1);
        settings.Set(WindowsScriptHostSettingsScope.User, "Enabled", 0);

        Assert.Equal(UserEnabled, WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }

    [Fact]
    public void AMachineZeroIsNotOverriddenByUserSettingsOrIgnoreUserSettings()
    {
        FakeWindowsScriptHostSettings settings = new();
        settings.Set(WindowsScriptHostSettingsScope.Machine, "Enabled", 0);
        settings.Set(WindowsScriptHostSettingsScope.Machine, "IgnoreUserSettings", 1);
        settings.Set(WindowsScriptHostSettingsScope.User, "Enabled", 1);

        Assert.Equal(MachineEnabled, WindowsScriptHostPolicy.FindDisablingSetting(settings));
    }
}
