using System.Diagnostics;
using System.Text;
using OpenClaw.Launcher.Gateway;

namespace OpenClaw.Launcher.Tests.Gateway;

/// <summary>
/// Covers the classification that separates a missing scheduled task from one
/// this account may not read.
/// </summary>
/// <remarks>
/// The runner is injected, so nothing here registers, runs, or deletes a real
/// scheduled task. The diagnostics are deliberately German: the point of these
/// tests is that classification never reads the message.
/// </remarks>
public sealed class SchTasksGatewaySchedulerTests
{
    private const string TaskName = @"\OpenClaw Gateway OpenClaw.Gateway_test";

    private const string GermanNotFound =
        "FEHLER: Das System kann die angegebene Datei nicht finden.";

    private const string GermanAccessDenied =
        "FEHLER: Der Zugriff wurde verweigert.";

    private static string Listing(params string[] taskNames) =>
        string.Join(
            "\r\n",
            taskNames.Select(name => $"\"{name}\",\"N/A\",\"Bereit\""));

    private static SchTasksGatewayScheduler CreateScheduler(
        SchTasksGatewayScheduler.SchTasksOutcome query,
        SchTasksGatewayScheduler.SchTasksOutcome listing,
        List<string>? invocations = null) =>
        new((arguments, _) =>
        {
            invocations?.Add(string.Join(' ', arguments));
            bool isListing = arguments.Contains("CSV");
            return Task.FromResult(isListing ? listing : query);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XmlExportPreservesUnicodeAndReportsReadFailure(bool refused)
    {
        const string taskName = @"\OpenClaw ü漢字 O'Brien O’Brien ‘ + $(throw 'injected') + ’";
        const string xml = "<Task><Actions><Exec><Arguments>ü漢字 O'Brien</Arguments></Exec></Actions></Task>";
        ProcessStartInfo start = SchTasksGatewayScheduler.CreateStartInfo(
            "must-not-run-schtasks.exe", ["/Query", "/TN", taskName, "/XML", "ONE"]);
        Assert.True(start.CreateNoWindow);
        Assert.False(start.UseShellExecute);
        Assert.Equal(Encoding.UTF8, start.StandardOutputEncoding);
        Assert.Equal(Encoding.UTF8, start.StandardErrorEncoding);
        string command = Encoding.Unicode.GetString(Convert.FromBase64String(start.ArgumentList[^1]));
        // Replace only the external COM boundary. Execute the generated query,
        // task-name quoting, UTF-8 transport, and failure handling unchanged.
        string fixture = $$"""
            $global:expectedName = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.Unicode.GetBytes(taskName))}}'))
            $global:fixtureXml = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.Unicode.GetBytes(xml))}}'))
            $global:refused = ${{refused}}
            function New-Object {
                param([string]$ComObject)
                if ($ComObject -ne 'Schedule.Service') { throw 'Wrong COM owner' }
                $service = [pscustomobject]@{}
                $service | Add-Member ScriptMethod Connect {}
                $service | Add-Member ScriptMethod GetFolder {
                    param($path)
                    if ($path -ne '\') { throw 'Wrong task folder' }
                    $folder = [pscustomobject]@{}
                    $folder | Add-Member ScriptMethod GetTask {
                        param($name)
                        if ($name -cne $global:expectedName) { throw 'Task name changed' }
                        if ($global:refused) { throw 'fixture access denied' }
                        return [pscustomobject]@{Xml=$global:fixtureXml}
                    }
                    return $folder
                }
                return $service
            }
            """;
        start.ArgumentList[^1] = Convert.ToBase64String(Encoding.Unicode.GetBytes(fixture + "\n" + command));
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(budget.Token);
            Assert.Equal(refused ? 1 : 0, process.ExitCode);
            Assert.Equal(refused ? string.Empty : xml, await output.ConfigureAwait(true));
            if (refused)
            {
                Assert.Contains("fixture access denied", await error.ConfigureAwait(true), StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(string.Empty, await error.ConfigureAwait(true));
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task AnAlreadyCancelledRequestDoesNotInvokeTheScheduler()
    {
        bool invoked = false;
        SchTasksGatewayScheduler scheduler = new((_, _) =>
        {
            invoked = true;
            return Task.FromResult(new SchTasksGatewayScheduler.SchTasksOutcome(0, string.Empty, string.Empty));
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scheduler.QueryAsync(TaskName, cancellation.Token));

        Assert.False(invoked);
    }

    [Fact]
    public async Task AnAbsentTaskIsMissingWhenDiagnosticsAreNotEnglish()
    {
        SchTasksGatewayScheduler scheduler = CreateScheduler(
            new SchTasksGatewayScheduler.SchTasksOutcome(1, string.Empty, GermanNotFound),
            new SchTasksGatewayScheduler.SchTasksOutcome(
                0,
                Listing(@"\SomeoneElsesTask"),
                string.Empty));

        GatewayTaskProbe probe = await scheduler.QueryAsync(TaskName, CancellationToken.None);

        Assert.Equal(GatewayTaskPresence.Missing, probe.Presence);
    }

    // A refused read must never be reported as missing: the caller re-registers
    // on missing, and that write would be refused too.
    [Fact]
    public async Task ARefusedReadOfAnExistingTaskStaysUnreadable()
    {
        SchTasksGatewayScheduler scheduler = CreateScheduler(
            new SchTasksGatewayScheduler.SchTasksOutcome(1, string.Empty, GermanAccessDenied),
            new SchTasksGatewayScheduler.SchTasksOutcome(
                0,
                Listing(TaskName),
                string.Empty));

        GatewayTaskProbe probe = await scheduler.QueryAsync(TaskName, CancellationToken.None);

        Assert.Equal(GatewayTaskPresence.Unreadable, probe.Presence);
    }

    [Fact]
    public async Task AFailedListingLeavesTheQuestionOpen()
    {
        SchTasksGatewayScheduler scheduler = CreateScheduler(
            new SchTasksGatewayScheduler.SchTasksOutcome(1, string.Empty, GermanNotFound),
            new SchTasksGatewayScheduler.SchTasksOutcome(1, string.Empty, GermanAccessDenied));

        GatewayTaskProbe probe = await scheduler.QueryAsync(TaskName, CancellationToken.None);

        Assert.Equal(GatewayTaskPresence.Unreadable, probe.Presence);
    }

    // The listing costs a second process launch, so it must not run when the
    // task was read successfully.
    [Fact]
    public async Task ASuccessfulReadDoesNotEnumerate()
    {
        List<string> invocations = [];
        SchTasksGatewayScheduler scheduler = CreateScheduler(
            new SchTasksGatewayScheduler.SchTasksOutcome(0, "<Task />", string.Empty),
            new SchTasksGatewayScheduler.SchTasksOutcome(0, string.Empty, string.Empty),
            invocations);

        await scheduler.QueryAsync(TaskName, CancellationToken.None);

        Assert.DoesNotContain(invocations, call => call.Contains("CSV", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingAnAlreadyAbsentTaskSucceedsOnLocalizedWindows()
    {
        SchTasksGatewayScheduler scheduler = CreateScheduler(
            new SchTasksGatewayScheduler.SchTasksOutcome(1, string.Empty, GermanNotFound),
            new SchTasksGatewayScheduler.SchTasksOutcome(
                0,
                Listing(@"\SomeoneElsesTask"),
                string.Empty));

        GatewayTaskOperation result = await scheduler.DeleteAsync(TaskName, CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task DeletingATaskThatStillExistsReportsFailure()
    {
        SchTasksGatewayScheduler scheduler = CreateScheduler(
            new SchTasksGatewayScheduler.SchTasksOutcome(1, string.Empty, GermanAccessDenied),
            new SchTasksGatewayScheduler.SchTasksOutcome(
                0,
                Listing(TaskName),
                string.Empty));

        GatewayTaskOperation result = await scheduler.DeleteAsync(TaskName, CancellationToken.None);

        Assert.False(result.Succeeded);
    }

    // Task Scheduler reports absolute names; a caller's leading separator is
    // incidental and must not change the answer.
    [Theory]
    [InlineData(@"\OpenClaw Gateway", @"\OpenClaw Gateway")]
    [InlineData(@"OpenClaw Gateway", @"\OpenClaw Gateway")]
    [InlineData(@"\OpenClaw Gateway", @"OpenClaw Gateway")]
    public void ListingMatchesRegardlessOfLeadingSeparator(string listed, string wanted)
    {
        Assert.True(SchTasksGatewayScheduler.ListingContains(Listing(listed), wanted));
    }

    // Only the first column identifies a task. The localized status column must
    // never produce a match.
    [Fact]
    public void ListingIgnoresColumnsOtherThanTheName()
    {
        string listing = "\"\\Other\",\"N/A\",\"OpenClaw Gateway\"";

        Assert.False(
            SchTasksGatewayScheduler.ListingContains(listing, @"\OpenClaw Gateway"));
    }

    [Fact]
    public void ListingDoesNotMatchAPrefixOfAnotherTask()
    {
        Assert.False(
            SchTasksGatewayScheduler.ListingContains(
                Listing(@"\OpenClaw Gateway Extra"),
                @"\OpenClaw Gateway"));
    }
}
