using System.CommandLine;

namespace OpenClaw.Launcher;

internal static partial class ClawCtlCommandLine
{
    private static void AddStateArchiveCommands(
        RootCommand root,
        ClawCtlHandlers handlers,
        ClawCtlOutputOptions outputOptions,
        Option<bool> rootJson,
        Option<bool> noColor)
    {
        Command backup = new("backup", "Save a verified backup of the current agent's OpenClaw state.");
        Argument<string?> destination = new("archive")
        {
            Description = "Optional .tar.gz output file; otherwise use the retained backup store.",
            Arity = ArgumentArity.ZeroOrOne
        };
        Option<bool> list = new("--list") { Description = "List retained archives without opening a session." };
        Option<bool> dryRun = new("--dry-run") { Description = "Preview the upstream backup inventory." };
        Option<bool> json = CreateJsonOption();
        backup.Arguments.Add(destination);
        backup.Options.Add(list);
        backup.Options.Add(dryRun);
        backup.Options.Add(json);
        backup.Validators.Add(parsed =>
        {
            if (parsed.GetValue(list) &&
                (parsed.GetValue(dryRun) || parsed.GetValue(destination) is not null))
            {
                parsed.AddError("Option '--list' cannot be combined with an archive or '--dry-run'.");
            }
        });
        backup.SetAction((parsed, token) =>
        {
            outputOptions.Json = IsJsonRequested(parsed, rootJson, json);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return (handlers.Backup ??
                throw new InvalidOperationException("The backup handler is not registered."))(
                    new BackupOptions(parsed.GetValue(destination), parsed.GetValue(list), parsed.GetValue(dryRun)),
                    token);
        });
        root.Subcommands.Add(backup);

        Command restore = new("restore", "Verify and activate an archive in the current agent; leave the gateway stopped.");
        Argument<string?> source = new("archive")
        {
            Description = "Input archive; otherwise propose the newest manual or recovery archive.",
            Arity = ArgumentArity.ZeroOrOne
        };
        Option<bool> restoreDryRun = new("--dry-run") { Description = "Verify and preview mappings without changing live state." };
        Option<bool> restoreYes = new("--yes") { Description = "Confirm replacement; required for JSON or redirected execution." };
        Option<bool> rollback = new("--rollback") { Description = "Roll back the current agent's interrupted activation." };
        Option<bool> restoreJson = CreateJsonOption();
        restore.Arguments.Add(source);
        restore.Options.Add(restoreDryRun);
        restore.Options.Add(restoreYes);
        restore.Options.Add(rollback);
        restore.Options.Add(restoreJson);
        restore.Validators.Add(parsed =>
        {
            if (parsed.GetValue(rollback) && (parsed.GetValue(source) is not null || parsed.GetValue(restoreDryRun)))
            {
                parsed.AddError("Option '--rollback' cannot be combined with an archive or '--dry-run'.");
            }
        });
        restore.SetAction((parsed, token) =>
        {
            outputOptions.Json = IsJsonRequested(parsed, rootJson, restoreJson);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return (handlers.Restore ?? throw new InvalidOperationException("The restore handler is not registered."))(
                new RestoreOptions(parsed.GetValue(source), parsed.GetValue(restoreDryRun),
                    parsed.GetValue(restoreYes), parsed.GetValue(rollback)), token);
        });
        root.Subcommands.Add(restore);

        Command recover = new("recover", "Rescue an explicitly named offline profile into the current agent without modifying the source.");
        Argument<string> profile = new("profile") { Description = "Absolute path of the old agent profile." };
        Option<string?> recoveryOutput = new("--output") { Description = "Optional retained .tar.gz recovery archive." };
        Option<bool> recoveryDryRun = new("--dry-run") { Description = "Inspect source access and supported scope without activation." };
        Option<bool> recoveryYes = new("--yes") { Description = "Confirm replacement of the current agent's state." };
        Option<bool> recoveryJson = CreateJsonOption();
        recover.Arguments.Add(profile);
        recover.Options.Add(recoveryOutput);
        recover.Options.Add(recoveryDryRun);
        recover.Options.Add(recoveryYes);
        recover.Options.Add(recoveryJson);
        recover.Validators.Add(parsed =>
        {
            if (parsed.GetValue(recoveryDryRun) && parsed.GetValue(recoveryOutput) is not null)
            {
                parsed.AddError("Option '--output' cannot be combined with '--dry-run'.");
            }
        });
        recover.SetAction((parsed, token) =>
        {
            outputOptions.Json = IsJsonRequested(parsed, rootJson, recoveryJson);
            outputOptions.NoColor = parsed.GetValue(noColor);
            return (handlers.Recover ?? throw new InvalidOperationException("The recover handler is not registered."))(
                new RecoverOptions(parsed.GetValue(profile)!, parsed.GetValue(recoveryOutput),
                    parsed.GetValue(recoveryDryRun), parsed.GetValue(recoveryYes)), token);
        });
        root.Subcommands.Add(recover);
    }
}
