namespace OpenClaw.Launcher.Tests;

public sealed class HostEntrypointResolverTests
{
    [Fact]
    public void ControlAliasSelectsTheManagementEntrypoint() =>
        Assert.Equal(
            HostEntrypoint.Control,
            HostEntrypointResolver.Resolve(
                "\"C:\\Users\\someone\\AppData\\Local\\Microsoft\\WindowsApps\\clawctl.exe\" setup"));

    [Fact]
    public void AgentAliasSelectsThePassthroughEntrypoint() =>
        Assert.Equal(
            HostEntrypoint.Agent,
            HostEntrypointResolver.Resolve(
                "\"C:\\Users\\someone\\AppData\\Local\\Microsoft\\WindowsApps\\openclaw.exe\" setup"));

    [Fact]
    public void ControlApplicationUserModelIdSelectsTheManagementEntrypoint() =>
        Assert.Equal(
            HostEntrypoint.Control,
            HostEntrypointResolver.Resolve(
                "\"C:\\Program Files\\WindowsApps\\OpenClaw\\openclaw.exe\" setup",
                "OpenClaw.Gateway_abc123!Control"));

    [Fact]
    public void AgentApplicationUserModelIdDoesNotSelectTheManagementEntrypoint() =>
        Assert.Equal(
            HostEntrypoint.Agent,
            HostEntrypointResolver.Resolve(
                "\"C:\\Program Files\\WindowsApps\\OpenClaw\\openclaw.exe\" setup",
                "OpenClaw.Gateway_abc123!App"));

    [Fact]
    public void UnknownOrMalformedInvocationDefaultsToAgent()
    {
        Assert.Equal(
            HostEntrypoint.Agent,
            HostEntrypointResolver.Resolve("launcher.exe"));
        Assert.Equal(
            HostEntrypoint.Agent,
            HostEntrypointResolver.Resolve("\"C:\\x\\clawctl.exe setup"));
    }

    [Fact]
    public void OnlyArgvZeroSelectsTheEntrypoint() =>
        Assert.Equal(
            HostEntrypoint.Agent,
            HostEntrypointResolver.Resolve(
                "openclaw.exe run clawctl.exe repair"));
}
