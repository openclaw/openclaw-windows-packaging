using System.Diagnostics;

namespace OpenClaw.Launcher.Tests;

public sealed class PowerShellCompletionIntegrationTests
{
    [Fact]
    public async Task NativeCompleterTranslatesAbsoluteCursorPositions()
    {
        string directory = TestDirectory.Create();
        string scriptPath = Path.Combine(directory, "completion-test.ps1");
        try
        {
            string script = $$"""
                function global:clawctl {
                    if (
                        $args.Count -eq 2 -and
                        $args[0] -eq '[suggest:11]' -and
                        $args[1] -eq 'clawctl sta'
                    ) {
                        'status'
                    }
                    else {
                        "unexpected:$($args -join '|')"
                    }
                }

                {{PowerShellCompletion.ClawCtlScript}}

                $cases = @(
                    @{ Text = '  clawctl sta'; Cursor = 13 },
                    @{ Text = 'Write-Output x; clawctl sta'; Cursor = 27 }
                )
                foreach ($case in $cases) {
                    $matches = @(
                        (TabExpansion2 `
                            -inputScript $case.Text `
                            -cursorColumn $case.Cursor).CompletionMatches |
                            ForEach-Object CompletionText
                    )
                    if ($matches -notcontains 'status') {
                        throw (
                            "Completion failed for '$($case.Text)': " +
                            ($matches -join ', ')
                        )
                    }
                }
                """;
            await File.WriteAllTextAsync(scriptPath, script).ConfigureAwait(true);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                ArgumentList =
                {
                    "-NoLogo",
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    scriptPath
                },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Unable to start pwsh.exe.");

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(true);
            string output = await outputTask.ConfigureAwait(true);
            string error = await errorTask.ConfigureAwait(true);

            Assert.True(
                process.ExitCode == 0,
                $"PowerShell completion test failed ({process.ExitCode}).{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{error}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
