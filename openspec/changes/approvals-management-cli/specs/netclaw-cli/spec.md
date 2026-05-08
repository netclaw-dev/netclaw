## ADDED Requirements

### Requirement: Operator CLI for persistent tool approvals

The CLI SHALL provide a `netclaw approvals` command surface for inspecting
and revoking entries in the persistent approvals file
(`~/.netclaw/config/tool-approvals.json`). The command SHALL operate on the
file directly via `Netclaw.Configuration.ToolApprovalStore` without
requiring the daemon to be running. Bare `netclaw approvals` (and
`netclaw approvals tui`) SHALL launch an interactive Termina TUI page.
Single-shot subcommands SHALL be `list`, `revoke`, and `help`.

`list` SHALL accept `--audience <personal|team|public>`, `--tool <name>`,
and `--json`. Without flags it SHALL print every audience and tool group
in a stable order.

`revoke <pattern>` SHALL remove only entries that match `<pattern>` exactly
under the same case-sensitivity rules that the daemon uses for shell
approval matching (Ordinal on POSIX, OrdinalIgnoreCase on Windows).
`revoke` SHALL accept `--audience` and `--tool` to scope the removal.
`revoke --tool <name> --all` SHALL clear every entry for that tool in the
targeted audiences. `revoke` of a pattern that does not match any entry
SHALL exit non-zero with a clear message; the CLI SHALL NOT silently
succeed.

The CLI SHALL NOT add or upgrade approvals; it is read-and-revoke only.
Exit codes SHALL be 0 for success, 1 for user errors (bad flag combos,
unknown audience, no match for revoke), and 2 for malformed-file
conditions surfaced by the underlying store.

#### Scenario: Empty approvals file lists no entries with exit zero

- **GIVEN** `tool-approvals.json` does not exist or contains `{}`
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the CLI prints `No persistent approvals.`
- **AND** exits with code `0`

#### Scenario: List filters by audience

- **GIVEN** `tool-approvals.json` contains entries under `personal` and `team`
- **WHEN** the operator runs `netclaw approvals list --audience personal`
- **THEN** only the `personal` audience entries are printed

#### Scenario: List emits JSON with audience/tool/pattern shape

- **GIVEN** `tool-approvals.json` contains
  `{"audiences":{"personal":{"shell_execute":["git push"]}}}`
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** the output is valid JSON
- **AND** the structure groups patterns by audience and tool

#### Scenario: Revoke removes only exact matches

- **GIVEN** `tool-approvals.json` contains
  `{"audiences":{"personal":{"shell_execute":["git push","/home/.netclaw/logs/"]}}}`
- **WHEN** the operator runs `netclaw approvals revoke "git push" --tool shell_execute --audience personal`
- **THEN** the `git push` entry is removed
- **AND** `/home/.netclaw/logs/` remains
- **AND** the CLI exits with code `0`

#### Scenario: Revoke with no match exits non-zero

- **GIVEN** `tool-approvals.json` does not contain `git push`
- **WHEN** the operator runs `netclaw approvals revoke "git push"`
- **THEN** the CLI prints a no-match message
- **AND** exits with code `1`
- **AND** does not modify the file

#### Scenario: Revoke --tool --all clears all entries for the tool

- **GIVEN** `tool-approvals.json` contains multiple `shell_execute` entries
  under `personal`
- **WHEN** the operator runs `netclaw approvals revoke --tool shell_execute --audience personal --all`
- **THEN** every `shell_execute` entry under `personal` is removed
- **AND** entries for other tools and other audiences are untouched

#### Scenario: Revoke --all without --tool is rejected

- **WHEN** the operator runs `netclaw approvals revoke --all`
- **THEN** the CLI rejects the invocation with a clear usage message
- **AND** exits with code `1`
- **AND** does not modify the file

#### Scenario: Daemon picks up CLI-applied revocation without restart

- **GIVEN** the daemon is running and has previously approved `git push`
- **WHEN** the operator runs `netclaw approvals revoke "git push" --tool shell_execute --audience personal`
- **AND** a new session attempts `git push` afterwards
- **THEN** the daemon prompts for approval again
- **AND** the daemon was not restarted

#### Scenario: Bare invocation launches the TUI

- **WHEN** the operator runs `netclaw approvals` with no subcommand
- **THEN** the CLI launches the interactive Termina approvals page
