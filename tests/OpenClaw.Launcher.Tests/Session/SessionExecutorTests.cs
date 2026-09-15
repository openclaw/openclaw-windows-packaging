using OpenClaw.Launcher.Mxc;
using OpenClaw.Launcher.Session;
using OpenClaw.SessionProtocol;

namespace OpenClaw.Launcher.Tests.Session;

public sealed class SessionExecutorTests : IDisposable
{
    private readonly string _root = TestDirectory.Create();
    private readonly FakeMxcSessionClient _backend = new();
    private readonly List<string> _log = [];

    private string Workspace => _root;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SessionRecord Record(string? workspace = null) => new()
    {
        SchemaVersion = 1,
        SandboxId = "iso:sandbox1",
        ApplicationId = "PFN:OpenClaw.Gateway_abc123",
        WorkspacePath = workspace ?? Workspace,
        Generation = "test-generation",
    };

    private SessionExecutionRequest Request(params string[] arguments) =>
        new(
            @"C:\Package\session-host\x64\openclaw-session-host.exe",
            @"C:\Program Files\nodejs\node.exe",
            @"C:\Package\app",
            arguments,
            @"C:\work");

    private SessionExecutor Create() => new(_backend, _log.Add);

    [Fact]
    public async Task IsolatedLaunchRetainsTheHostInteractiveEnvironment()
    {
        SessionLaunchRequest? delivered = null;
        RespondAsHelper(request =>
        {
            delivered = request;
            return new SessionLaunchResult
            {
                RequestId = request.RequestId,
                Launched = true,
                ExitCode = 0,
            };
        });

        await Create().ExecuteAsync(
            Record(),
            new SessionExecutionRequest(
                @"C:\Package\session-host\x64\openclaw-session-host.exe",
                @"C:\Program Files\nodejs\node.exe",
                @"C:\Package\app",
                [],
                Workspace)
            {
                AdditionalEnvironment = OpenClawRuntimeEnvironment.Build(
                    isInteractive: true,
                    _ => null)
            },
            CancellationToken.None);

        Assert.Equal("3", delivered!.Environment!["FORCE_COLOR"]);
        Assert.Equal("1", delivered.Environment["WT_SESSION"]);
    }

    /// <summary>
    /// Stands in for the guest helper: reads the delivered request and writes
    /// the control result the real helper would.
    /// </summary>
    private void RespondAsHelper(
        Func<SessionLaunchRequest, SessionLaunchResult> respond)
    {
        _backend.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(Workspace, "launch-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            SessionLaunchResult result = respond(request);
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(result));
            return Task.FromResult(result.ExitCode ?? 0);
        };
    }

    private void RespondLaunched(int exitCode) =>
        RespondAsHelper(request => new SessionLaunchResult
        {
            RequestId = request.RequestId,
            Launched = true,
            ExitCode = exitCode,
        });

    [Fact]
    public async Task ApplicationExitCodeIsReturned()
    {
        RespondLaunched(42);

        int exitCode = await Create().ExecuteAsync(
            Record(),
            Request("--version"),
            CancellationToken.None);

        Assert.Equal(42, exitCode);
    }

    [Fact]
    public async Task ArgumentsTravelAsDataNotOnTheCommandLine()
    {
        // The pinned runtime flattens the command line through cmd.exe, where
        // %USERPROFILE% expands and a quote truncates a later argument.
        string[] hostile = ["%USERPROFILE%", "q\"x", "a&b", "trailing\\", "", "ünïcode"];
        SessionLaunchRequest? delivered = null;
        RespondAsHelper(request =>
        {
            delivered = request;
            return new SessionLaunchResult
            {
                RequestId = request.RequestId,
                Launched = true,
                ExitCode = 0,
            };
        });

        await Create().ExecuteAsync(Record(), Request(hostile), CancellationToken.None);

        Assert.Equal(hostile, delivered!.Arguments!.Skip(1));
        string commandLine = Assert.Single(_backend.AttachedCommandLines);
        foreach (string argument in hostile.Where(value => value.Length > 0))
        {
            Assert.DoesNotContain(argument, commandLine, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task EntryPointIsTheFirstNodeArgument()
    {
        SessionLaunchRequest? delivered = null;
        RespondAsHelper(request =>
        {
            delivered = request;
            return new SessionLaunchResult
            {
                RequestId = request.RequestId,
                Launched = true,
                ExitCode = 0,
            };
        });

        await Create().ExecuteAsync(Record(), Request("doctor"), CancellationToken.None);

        Assert.Equal(
            [@"C:\Package\app\openclaw.mjs", "doctor"],
            delivered!.Arguments);
        Assert.Equal(@"C:\Program Files\nodejs\node.exe", delivered.Executable);
    }

    [Fact]
    public async Task WorkingDirectoryIsCarriedExplicitly()
    {
        // Execution does not inherit the caller's directory; it defaults to
        // C:\Windows\System32 unless the request sets it.
        SessionLaunchRequest? delivered = null;
        RespondAsHelper(request =>
        {
            delivered = request;
            return new SessionLaunchResult
            {
                RequestId = request.RequestId,
                Launched = true,
                ExitCode = 0,
            };
        });

        await Create().ExecuteAsync(Record(), Request(), CancellationToken.None);

        Assert.Equal(@"C:\work", delivered!.WorkingDirectory);
    }

    [Fact]
    public async Task ExternalSupervisionVariablesAreDelivered()
    {
        SessionLaunchRequest? delivered = null;
        RespondAsHelper(request =>
        {
            delivered = request;
            return new SessionLaunchResult
            {
                RequestId = request.RequestId,
                Launched = true,
                ExitCode = 0,
            };
        });

        await Create().ExecuteAsync(Record(), Request(), CancellationToken.None);

        Assert.Equal("external", delivered!.Environment!["OPENCLAW_SUPERVISOR_MODE"]);
        Assert.Equal("external", delivered.Environment["OPENCLAW_SERVICE_REPAIR_POLICY"]);
        Assert.Equal("1", delivered.Environment["OPENCLAW_NO_AUTO_UPDATE"]);
    }

    [Fact]
    public async Task CommandLineCarriesOnlyTheHelperAndItsRequest()
    {
        RespondLaunched(0);

        await Create().ExecuteAsync(Record(), Request("chat"), CancellationToken.None);

        string commandLine = Assert.Single(_backend.AttachedCommandLines);

        // The properties that matter are what the command line carries, not how
        // it is shaped: the helper is named, and neither the user's arguments
        // nor the Node path ever reach a string the command processor expands.
        Assert.Contains(
            @"C:\Package\session-host\x64\openclaw-session-host.exe",
            commandLine,
            StringComparison.Ordinal);
        Assert.Contains("--request", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("chat", commandLine, StringComparison.Ordinal);
        Assert.DoesNotContain("node.exe", commandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecutionIsAttachedRatherThanBuffered()
    {
        RespondLaunched(0);

        await Create().ExecuteAsync(Record(), Request(), CancellationToken.None);

        Assert.Equal(["execute-attached:iso:sandbox1"], _backend.Calls);
    }

    [Fact]
    public async Task RequestAndResultFilesAreRemovedAfterwards()
    {
        RespondLaunched(0);

        await Create().ExecuteAsync(Record(), Request(), CancellationToken.None);

        Assert.Empty(Directory.GetFiles(Workspace));
    }

    [Fact]
    public async Task FilesAreRemovedEvenWhenExecutionFails()
    {
        _backend.AttachedBehavior = _ =>
            Task.FromException<int>(new MxcException(MxcErrorCode.BackendError, "no"));

        await Assert.ThrowsAsync<MxcException>(
            () => Create().ExecuteAsync(Record(), Request(), CancellationToken.None));

        Assert.Empty(Directory.GetFiles(Workspace));
    }

    [Fact]
    public async Task ConcurrentInvocationsUseDistinctRequestFiles()
    {
        var seen = new List<string>();
        _backend.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(Workspace, "launch-*.json")
                .Single(path => !path.EndsWith(".result.json", StringComparison.Ordinal));
            seen.Add(requestPath);
            SessionLaunchRequest request = SessionLaunchProtocol.ReadRequest(
                File.ReadAllText(requestPath));
            File.WriteAllText(
                SessionLaunchProtocol.ResultPathFor(requestPath),
                SessionLaunchProtocol.SerializeResult(new SessionLaunchResult
                {
                    RequestId = request.RequestId,
                    Launched = true,
                    ExitCode = 0,
                }));
            return Task.FromResult(0);
        };

        await Create().ExecuteAsync(Record(), Request(), CancellationToken.None);
        await Create().ExecuteAsync(Record(), Request(), CancellationToken.None);

        Assert.Equal(2, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task HelperFailureIsDistinguishedFromAnApplicationExit()
    {
        // Exit code 64 is both the helper's failure code and a value OpenClaw
        // could return, so only the control result can tell them apart.
        RespondAsHelper(request => new SessionLaunchResult
        {
            RequestId = request.RequestId,
            Launched = false,
            Error = "The executable was not found.",
        });

        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => Create().ExecuteAsync(Record(), Request(), CancellationToken.None));

        Assert.Contains("The executable was not found.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplicationExitOf64IsNotMistakenForHelperFailure()
    {
        RespondLaunched(SessionLaunchProtocol.HelperFailureExitCode);

        int exitCode = await Create().ExecuteAsync(
            Record(),
            Request(),
            CancellationToken.None);

        Assert.Equal(SessionLaunchProtocol.HelperFailureExitCode, exitCode);
    }

    [Fact]
    public async Task MissingControlResultIsReportedRatherThanTrusted()
    {
        _backend.AttachedBehavior = _ => Task.FromResult(1);

        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => Create().ExecuteAsync(Record(), Request(), CancellationToken.None));

        Assert.Contains("did not report a launch result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreadableControlResultIsReported()
    {
        _backend.AttachedBehavior = _ =>
        {
            string requestPath = Directory.GetFiles(Workspace, "launch-*.json").Single();
            File.WriteAllText(SessionLaunchProtocol.ResultPathFor(requestPath), "{ not json");
            return Task.FromResult(0);
        };

        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => Create().ExecuteAsync(Record(), Request(), CancellationToken.None));

        Assert.Contains("unreadable launch result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultForAnotherRequestIsRejected()
    {
        RespondAsHelper(_ => new SessionLaunchResult
        {
            RequestId = "0123456789abcdef0123456789abcdef",
            Launched = true,
            ExitCode = 0,
        });

        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => Create().ExecuteAsync(Record(), Request(), CancellationToken.None));

        Assert.Contains("different request", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionWithoutAWorkspaceIsReported()
    {
        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => Create().ExecuteAsync(
                Record(workspace: string.Empty) with { WorkspacePath = null },
                Request(),
                CancellationToken.None));

        Assert.Contains("shared workspace", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
    }

    [Fact]
    public async Task StaleSessionGenerationIsRejectedBeforeWritingARequest()
    {
        SessionExecutor executor = new(
            _backend,
            _log.Add,
            isCurrentRecord: _ => false);

        SessionException exception = await Assert.ThrowsAsync<SessionException>(
            () => executor.ExecuteAsync(Record(), Request(), CancellationToken.None));

        Assert.Contains("generation changed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_backend.Calls);
        Assert.Empty(Directory.GetFiles(Workspace));
    }

    [Theory]
    [InlineData("C:\\has%percent\\helper.exe")]
    [InlineData("C:\\has\"quote\\helper.exe")]
    public void UnsafeHelperPathIsRefusedRatherThanCorrupted(string helperPath)
    {
        SessionException exception = Assert.Throws<SessionException>(
            () => SessionExecutor.BuildGuestCommandLine(helperPath, @"C:\ws\r.json"));

        Assert.Contains("quote or percent sign", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsafeRequestPathIsRefused()
    {
        Assert.Throws<SessionException>(
            () => SessionExecutor.BuildGuestCommandLine(
                @"C:\p\helper.exe",
                @"C:\ws\%TEMP%.json"));
    }

    // Quoting is proven by dispatching the command line through the command
    // processor the backend actually uses, in SessionGuestCommandLineTests.
    // A string-equality assertion here previously passed against a command line
    // that the command processor then broke apart at the first space.

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        _backend.AttachedBehavior = token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create().ExecuteAsync(Record(), Request(), cancellation.Token));

        Assert.Empty(Directory.GetFiles(Workspace));
    }
}

public sealed class OpenClawRuntimeEnvironmentTests
{
    [Fact]
    public void ExternalSupervisionIsDeclared()
    {
        IReadOnlyDictionary<string, string> environment =
            OpenClawRuntimeEnvironment.Build();

        Assert.Equal("external", environment["OPENCLAW_SUPERVISOR_MODE"]);
        Assert.Equal("external", environment["OPENCLAW_SERVICE_REPAIR_POLICY"]);
        Assert.Equal("1", environment["OPENCLAW_NO_AUTO_UPDATE"]);
    }

    [Fact]
    public void ForegroundLaunchUsesTheSameVariables()
    {
        // Both launch paths must agree; a path that supervises or updates
        // itself would be a silent behavior difference.
        string application = TestDirectory.Create();
        try
        {
            File.WriteAllText(Path.Combine(application, "openclaw.mjs"), "// test");
            System.Diagnostics.ProcessStartInfo startInfo =
                GatewayLauncher.CreateStartInfo(
                    @"C:\node.exe",
                    application,
                    []);

            foreach ((string name, string value) in OpenClawRuntimeEnvironment.Build())
            {
                Assert.Equal(value, startInfo.Environment[name]);
            }
        }
        finally
        {
            Directory.Delete(application, recursive: true);
        }
    }

    [Fact]
    public void ApplyToOverwritesAnInheritedValue()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENCLAW_SUPERVISOR_MODE"] = "self",
        };

        OpenClawRuntimeEnvironment.ApplyTo(environment);

        Assert.Equal("external", environment["OPENCLAW_SUPERVISOR_MODE"]);
    }
}
