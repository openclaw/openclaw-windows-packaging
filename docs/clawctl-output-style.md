# `clawctl` output style

`clawctl` manages the packaged Windows environment around OpenClaw. Its output
should describe that environment in terms an operator can act on, while
`openclaw` remains the entry point for the application itself.

## Human-readable output

- Start command results with `🦀 clawctl <command>` when Unicode is available,
  followed by aligned `Label: value` rows. Fall back to `clawctl <command>` for
  legacy or non-Unicode output.
- Use lowercase status words such as `ready`, `running`, `stopped`, and
  `not configured`.
- Use a check mark for success, an exclamation mark for attention, and a cross
  for failure. Non-Unicode output uses `[ok]`, `[!]`, and `[x]`.
- Narrate long-running human operations at their real lifecycle boundaries.
  Use one updating status in an interactive terminal and durable stage lines
  when output is redirected. Structured output remains exactly one document.
- Give each interactive Unicode stage a randomly selected crab animation:
  Scuttle, Bubbles, or Tide Pulse. Select again when the lifecycle stage
  changes; repeated selections are valid. Use the alternating `v(.-.)v` and
  `V(.-.)V` crab emoticons when Unicode is unavailable.
- End successful setup with a tentative `openclaw onboard` suggestion. The
  user may need flags or may choose a different advanced path, so setup neither
  presents onboarding as mandatory nor launches it automatically.
- End neutral states with the command that moves the user forward, such as
  `Run: clawctl setup`.
- When a successful interactive `openclaw` launch finds a `NotStarted` or
  `Stopped` gateway with `startup eligible` readiness, narrate the managed
  gateway start on standard error. Do nothing for non-zero child exits,
  redirected output, non-eligible readiness, or `Starting`, `Unhealthy`, and
  `Unknown` gateway states; acknowledge an observed running gateway.
- After live gateway narration clears, write one durable outcome line. Unicode
  output starts with the crab identity and uses a check mark for a verified
  listener, an exclamation mark for a still-starting or unverified launch, and
  a cross for a definite stop or handled start failure. Non-Unicode output uses
  `[ok]`, `[!]`, or `[x]` without the crab.
- A still-starting or unverified automatic gateway start directs the user to
  `clawctl gateway-service status`; do not encourage a second start while an
  owned process may still be alive. A definite stop or handled failure writes
  `clawctl gateway-service start` on its own line so width-constrained wrapping
  cannot break the retry command. These advisory outcomes must not change the
  OpenClaw exit code. Leave unsuccessful guidance unacknowledged so a later
  eligible launch retries. `CLAWCTL_AUTO_GATEWAY_START=0` (or `false`, `no`,
  or `off`) restores the start hint.
- Keep `clawctl pwsh` silent on success. Interactive mode reaches the shell
  prompt directly; `--command` and `--file` leave stdin, stdout, stderr, and the
  PowerShell exit code transparent. Its help text explains which commands are
  available inside the session. Reject `--json` in every mode rather than
  capturing arbitrary PowerShell streams.
- Treat Windows Ctrl-C termination of an attached foreground command as user
  cancellation: emit no failure guidance and return portable exit code 130.

Prefer keeping package paths, sandbox identifiers, process identifiers, and
other non-actionable implementation details out of human output. Preserve them
in structured output and diagnostics when they are useful for automation or
support.

The post-OpenClaw gateway hint is a package-owned status surface even though it
is written after upstream output. On an interactive invocation it uses the crab
mark, warning-colored `Hint:`, and an accent-colored unquoted command. Resolve
foreground eligibility separately from the selected stderr handle: an
app-execution-alias proxy can carry ANSI without supporting console-mode
changes, while a native console handle still requires VT setup. Whole-command
redirection, CI, and `NO_COLOR` remain plain; `FORCE_COLOR` remains authoritative.

## Failures and diagnostics

Expected refusals should state the condition and the next usable action without
asking the user to file an issue. Unexpected faults should explain how to
capture diagnostics and where to report the problem:

1. Run `clawctl collect-logs`.
2. Review the resulting bundle before sharing it.
3. File an issue at
   <https://github.com/openclaw/openclaw-windows-packaging/issues>.

`clawctl collect-logs` is the user-facing diagnostics entry point. Other
commands should not expose individual log paths when the bundle can collect the
same evidence. Its final bundle path is emitted as an exact standalone line,
outside a width-constrained grid, so it remains copyable.

## Help

Help is rendered by `clawctl`, not by System.CommandLine, so it uses the same
heading, grid, and palette as every other command. The built-in help action is
sealed and exposes only a wrap width, so replacing the action is the supported
extension point; the version action is replaced for a related reason.

`ClawCtlHelp` describes the command the user asked about by walking the live
tree, so a command added to `ClawCtlCommandLine` appears in help with no edit
here. Three obligations come with that:

- Give every command and option a description. An entry without one renders as
  a blank column.
- Honor `Hidden`. The library's renderer filters hidden symbols and so must
  this one.
- Report only options accepted at that command position. `--json` is
  command-local where structured output is supported, remains valid before a
  command, and is also valid between `gateway-service` and its subcommand.
  `--no-color` is recursive and inherited by every command; `--version` is not
  recursive and must not appear on subcommands.

Never take the command name from `RootCommand.Name`. It defaults to the entry
assembly, which is the test host under `dotnet test` and the scenario driver
under the NativeAOT suite. Use the known control command name instead.

## Progress

A lifecycle command that waits should say what it is waiting for. Report
progress as semantic stages from the operation and let the renderer present
them; an operation that writes to a console cannot also run from a logon task,
where nothing is watching.

Use a spinner only on an interactive console that has already been cleared for
color, and plain stage lines everywhere else, so redirected output and log files
stay readable. Spectre serializes live displays: finish the status before
rendering the result, and never open a second live surface inside the first.
Narration and a JSON document share standard output, so narration is off
entirely under `--json`.

## Addresses

Report a gateway port only when it can be identified unambiguously from the
configured port and observed listeners. Never use the upstream default as an
observation, and report no port when multiple unclassified listeners remain.

Do not construct a Control UI or WebSocket URL. OpenClaw owns TLS and Control UI
base-path configuration, so a locally assembled URL can point to the wrong
scheme or path. Human and JSON output follow the same port-only contract.

## Color

Output is composed from Spectre.Console renderables — a `Grid` for
`Label: value` rows and a `Panel` for the note callout — rather than from
hand-padded strings. Let the grid own column alignment instead of reintroducing
per-command width constants.

Color is an event, not a wash. Labels stay in the terminal's own foreground,
which is legible on whatever background the user runs, and only the leading
status word carries a hue. This follows OpenClaw's `styleHealthChannelLine`,
which colors the status word of a `label: detail` row and deliberately leaves
the label alone.

| Role | Color |
|---|---|
| Accent (heading, next-action command) | `#1687ff` |
| Muted (labels' detail lines, neutral states) | `#697a8b` |
| Success | `#048b41` |
| Warning | `#b26501` |
| Error, note border | `#ed1805` |

Color supplements text and status marks; it never carries meaning by itself.
Do not color JSON. Keep the text obtained after removing ANSI escape sequences
identical to ordinary non-color output.

Build values with `Paragraph.Append`, never by interpolating caller text into
markup: package paths and error messages contain `[` and `\`, which Spectre
would otherwise parse as markup. Use `Text` or `Markup.Escape` for any string
the user or the system supplied.

Reserve the note callout for a genuine fault, and cap it at 88 columns the way
OpenClaw caps its own notes. Unicode terminals get a rounded border; everything
else gets the ASCII border.

The status vocabulary intentionally follows OpenClaw's terminal output, while
the crab mark and blue palette give `clawctl` its own identity. The accent
favors the dark terminals most users run while retaining readable contrast on
light backgrounds. When OpenClaw changes its status vocabulary or semantic
colors, review this guide for useful parity rather than copying its
dark-terminal palette mechanically.
