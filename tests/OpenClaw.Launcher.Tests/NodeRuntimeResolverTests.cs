using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Tests;

public sealed class NodeRuntimeResolverTests
{
    [Fact]
    public async Task ResolveAcceptsCompatibleRuntime()
    {
        NodeRuntime runtime = await ResolveAsync(
            ["C:\\Node\\node.exe"],
            _ => Task.FromResult("v24.16.0"),
            Architecture.X64,
            Architecture.X64);

        Assert.Equal("C:\\Node\\node.exe", runtime.ExecutablePath);
        Assert.Equal(new Version(24, 16, 0), runtime.Version);
        Assert.Equal(Architecture.X64, runtime.Architecture);
    }

    [Fact]
    public async Task ResolveReportsInstallCommandWhenRuntimeIsMissing()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => ResolveAsync(
                [],
                _ => Task.FromResult("v24.16.0"),
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("not found on PATH", exception.Message, StringComparison.Ordinal);
        Assert.Contains(NodeRuntimeResolver.InstallCommand, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRejectsOutdatedRuntime()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => ResolveAsync(
                ["C:\\Node\\node.exe"],
                _ => Task.FromResult("v24.14.0"),
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("version 24.14.0 is unsupported", exception.Message, StringComparison.Ordinal);
        Assert.Contains(NodeRuntimeResolver.SupportedVersions, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRejectsMalformedVersion()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => ResolveAsync(
                ["C:\\Node\\node.exe"],
                _ => Task.FromResult("not-node"),
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("invalid version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRejectsPrereleaseVersion()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => ResolveAsync(
                ["C:\\Node\\node.exe"],
                _ => Task.FromResult("v24.15.0-rc.1"),
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("invalid version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRejectsIncompatibleArchitecture()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => ResolveAsync(
                ["C:\\Node\\node.exe"],
                _ => Task.FromResult("v24.16.0"),
                Architecture.Arm64,
                Architecture.X64));

        Assert.Contains("architecture Arm64 does not match X64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveReportsVersionQueryFailure()
    {
        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(
            () => ResolveAsync(
                ["C:\\Node\\node.exe"],
                _ => Task.FromException<string>(
                    new InvalidOperationException("query failed")),
                Architecture.X64,
                Architecture.X64));

        Assert.Contains("query failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(NodeRuntimeResolver.InstallCommand, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(22, 22, 2, false)]
    [InlineData(22, 22, 3, true)]
    [InlineData(23, 99, 0, false)]
    [InlineData(24, 14, 99, false)]
    [InlineData(24, 15, 0, true)]
    [InlineData(25, 8, 99, false)]
    [InlineData(25, 9, 0, true)]
    [InlineData(26, 0, 0, true)]
    public void SupportedVersionsMatchPackagedOpenClawRequirement(
        int major,
        int minor,
        int build,
        bool expected)
    {
        Assert.Equal(
            expected,
            NodeRuntimeResolver.IsSupported(new Version(major, minor, build)));
    }

    private static Task<NodeRuntime> ResolveAsync(
        IReadOnlyList<string> candidates,
        Func<string, Task<string>> queryVersion,
        Architecture candidateArchitecture,
        Architecture requiredArchitecture) =>
        NodeRuntimeResolver.ResolveAsync(
            candidates,
            (path, _) => queryVersion(path),
            _ => candidateArchitecture,
            requiredArchitecture,
            CancellationToken.None);
}
