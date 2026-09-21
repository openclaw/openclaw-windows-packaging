using System.Text;

namespace OpenClaw.Launcher.Tests;

// Startup is the part of the host that owns diagnostics, argument routing, the
// operational error boundary, and disposal. These scenarios drive the real
// Program.RunAsync with fixture-owned storage, so a regression there is caught
// without a test ever writing to the developer's profile.
public sealed class ProgramStartupTests : IDisposable
{
    private readonly string _testDirectory = TestDirectory.Create();

    [Fact]
    public void ProductionStartupProvidesTheLifecycleFactoryForFallbackValidation()
    {
        Assert.NotNull(HostStartup.CreateProduction().InstallationLifecycle);
    }

    [Fact]
    public void ProductionStartupIdentifiesItsProcessConsoleWriters()
    {
        Assert.True(HostStartup.CreateProduction().UsesProcessConsoleWriters);
    }

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
    public async Task OperationalFailureRecommendsCollectingLogsAndExitsNonZero()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");

        int exitCode = await Program.RunAsync(
            ["setup"],
            CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Contains("clawctl collect-logs", error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "https://github.com/openclaw/openclaw-windows-packaging/issues",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("package is ready", output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "Unhandled failure",
            await File.ReadAllTextAsync(logPath),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OperationalFailureStillExitsAndDisposesDiagnosticsWhenRenderingFails(
        bool disposed)
    {
        using var output = new StringWriter();
        TextWriter error = disposed
            ? new UnavailableTextWriter(new ObjectDisposedException("stderr"))
            : new UnavailableTextWriter(new IOException("stderr is unavailable"));
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        HostDiagnosticLog diagnostics = HostDiagnosticLog.Create(logPath);
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => diagnostics,
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(["setup"], startup);

        Assert.Equal(1, exitCode);
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Unhandled failure", log, StringComparison.Ordinal);
        Assert.Contains(
            disposed
                ? "Failure output to standard error failed: ObjectDisposedException."
                : "Failure output to standard error failed: IOException.",
            log,
            StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => diagnostics.Write("after return"));
    }

    [Fact]
    public async Task JsonFailureStillExitsWhenStandardOutputFails()
    {
        using var error = new StringWriter();
        var output = new UnavailableTextWriter(new IOException("stdout is unavailable"));
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        HostDiagnosticLog diagnostics = HostDiagnosticLog.Create(logPath);
        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Control,
            CreateDiagnostics = () => diagnostics,
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(["setup", "--json"], startup);

        Assert.Equal(1, exitCode);
        string log = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Unhandled failure", log, StringComparison.Ordinal);
        Assert.Contains(
            "Failure output to standard output failed: IOException.",
            log,
            StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => diagnostics.Write("after return"));
    }

    [Theory]
    [InlineData("status --json", "status")]
    [InlineData("--json status", "status")]
    [InlineData("gateway-service start --json", "gateway-service start")]
    [InlineData("gateway-service restart --json", "gateway-service restart")]
    public async Task JsonOperationalFailurePreservesTheSelectedCommand(
        string commandLine,
        string expectedCommand)
    {
        string[] args = commandLine.Split(' ');
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", $"{Guid.NewGuid():N}.log");

        int exitCode = await Program.RunAsync(args, CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedCommand, document.RootElement.GetProperty("command").GetString());
    }

    [Theory]
    [InlineData("--json=true", true)]
    [InlineData("--json:true", true)]
    [InlineData("--json false", false)]
    public async Task OperationalFailureHonorsParsedJsonBoolean(
        string option,
        bool expectedJson)
    {
        string[] args = option.Contains(' ', StringComparison.Ordinal)
            ? ["status", .. option.Split(' ')]
            : ["status", option];
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", $"{Guid.NewGuid():N}.log");

        int exitCode = await Program.RunAsync(args, CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Equal(expectedJson, !string.IsNullOrWhiteSpace(output.ToString()));
        Assert.Equal(!expectedJson, !string.IsNullOrWhiteSpace(error.ToString()));
        if (expectedJson)
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        }
    }

    [Theory]
    [InlineData("--json=true", true)]
    [InlineData("--json:true", true)]
    [InlineData("--json false", false)]
    public async Task PackageDiscoveryFailureHonorsJsonBoolean(
        string option,
        bool expectedJson)
    {
        string runtimeDirectory = Path.Combine(_testDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        string architecture = System.Runtime.InteropServices.RuntimeInformation
            .ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            var value => value.ToString()
        };
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, $"node-v24.0.0-win-{architecture}.zip"),
            string.Empty);
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDirectory, $"node-v24.1.0-win-{architecture}.zip"),
            string.Empty);
        string[] args = option.Contains(' ', StringComparison.Ordinal)
            ? ["status", .. option.Split(' ')]
            : ["status", option];
        using var output = new StringWriter();
        using var error = new StringWriter();
        string logPath = Path.Combine(_testDirectory, "logs", $"{Guid.NewGuid():N}.log");

        int exitCode = await Program.RunAsync(args, CreateStartup(logPath, output, error));

        Assert.Equal(1, exitCode);
        Assert.Equal(expectedJson, !string.IsNullOrWhiteSpace(output.ToString()));
        Assert.Equal(!expectedJson, !string.IsNullOrWhiteSpace(error.ToString()));
        if (expectedJson)
        {
            using System.Text.Json.JsonDocument document =
                System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.Equal("status", document.RootElement.GetProperty("command").GetString());
        }
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

        // The point of the test is that the command still produced its result
        // after diagnostics failed, not what the version happens to be.
        Assert.Contains(
            ClawCtlBuildMetadata.PackageVersion,
            output.ToString(),
            StringComparison.Ordinal);
    }

    // Startup routes the agent alias without letting the clawctl parser see
    // the arguments. On a machine that cannot host a session it fails loudly,
    // and the failure is the host's, not the parser's.
    [Fact]
    public async Task AgentStartupFailsLoudlyWithoutParsingItsArguments()
    {
        string applicationDirectory = Path.Combine(_testDirectory, "app");
        Directory.CreateDirectory(applicationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(applicationDirectory, "openclaw.mjs"),
            "console.log('fixture');");
        string logPath = Path.Combine(_testDirectory, "logs", "openclaw.log");
        string[] arguments = ["--help", "--", "@file.rsp", "[suggest:1]", "", "a b"];
        using var output = new StringWriter();
        using var error = new StringWriter();

        HostStartup startup = new()
        {
            Entrypoint = HostEntrypoint.Agent,
            CreateDiagnostics = () => HostDiagnosticLog.Create(logPath),
            BaseDirectory = _testDirectory,
            Output = output,
            Error = error
        };

        int exitCode = await Program.RunAsync(arguments, startup);

        Assert.Equal(1, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains(logPath, error.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            "newer version of Windows",
            error.ToString(),
            StringComparison.Ordinal);
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
            Error = error
        };

    public void Dispose()
    {
        Directory.Delete(_testDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class UnavailableTextWriter(Exception exception) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => throw exception;
    }
}
