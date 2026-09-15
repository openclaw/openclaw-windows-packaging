using System.Runtime.InteropServices;

namespace OpenClaw.Launcher.Tests;

// Startup is the part of the host that owns diagnostics, argument routing, the
// operational error boundary, and disposal. These scenarios drive the real
// Program.RunAsync with fixture-owned storage, so a regression there is caught
// without a test ever writing to the developer's profile.
public sealed class ProgramStartupTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public async Task StartupWritesRecordsToTheSuppliedDiagnosticLog()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");

        int exitCode = await Program.RunAsync(
            ["--version"],
            CreateStartup(logPath, output, error));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(logPath));
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Host started", log, StringComparison.Ordinal);
        Assert.Contains("Host exiting", log, StringComparison.Ordinal);
    }

    // The library's default exception handler is disabled so this boundary,
    // not System.CommandLine, reports operational failures.
    [Fact]
    public async Task OperationalFailureReportsTheDiagnosticPathAndExitsNonZero()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");

        int exitCode = await Program.RunAsync(
            ["setup"],
            CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Contains(logPath, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("package is ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "Unhandled failure",
            await File.ReadAllTextAsync(logPath),
            StringComparison.Ordinal);
    }

    // Losing the log must not take the command down with it: the host warns
    // once and keeps running.
    [Fact]
    public async Task UnavailableDiagnosticsWarnsOnceAndStillRunsTheCommand()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => throw new IOException("The log is unavailable."),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(["--version"], startup);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            "Unable to create diagnostics",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(
            typeof(Program).Assembly.GetName().Version?.ToString(),
            output.ToString().Trim());
    }

    // Startup routes the agent alias without letting the clawctl parser see
    // the arguments, and the child's exit code is the host's exit code.
    [Fact]
    public async Task AgentStartupForwardsArgumentsAndChildExitCode()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string[] arguments = ["--help", "--", "@file.rsp", "[suggest:1]", "", "a b"];
        string[]? forwarded = null;
        using var output = new StringWriter();
        using var error = new StringWriter();

        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Agent,
            CreateDiagnostics = () => HostDiagnosticLog.Create(
                Path.Combine(_testDirectory, "logs", "openclaw.log")),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error,
            ResolveNode = _ => Task.FromResult(
                new NodeRuntime(
                    Path.Combine(_testDirectory, "node.exe"),
                    new Version(24, 15, 0),
                    RuntimeInformation.ProcessArchitecture)),
            LaunchOpenClaw = (_, _, launchArguments, _, _, _) =>
            {
                forwarded = [.. launchArguments];
                return Task.FromResult(23);
            }
        };

        int exitCode = await Program.RunAsync(arguments, startup);

        Assert.Equal(23, exitCode);
        Assert.Equal(arguments, forwarded);
        Assert.Empty(output.ToString());
    }

    private HostStartup CreateStartup(
        string logPath,
        TextWriter output,
        TextWriter error) => new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => HostDiagnosticLog.Create(logPath),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error,
            ResolveNode = _ => Task.FromResult(
                new NodeRuntime(
                    Path.Combine(_testDirectory, "node.exe"),
                    new Version(24, 15, 0),
                    RuntimeInformation.ProcessArchitecture))
        };

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
