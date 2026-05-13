## MODIFIED Requirements

### Requirement: Operator CLI for persistent tool approvals

The CLI SHALL provide a `netclaw approvals` command surface for
inspecting, revoking, and adding entries to the persistent approvals
file (`~/.netclaw/config/tool-approvals.json`). The command SHALL
operate on the file directly via `Netclaw.Configuration.ToolApprovalStore`
without requiring the daemon to be running. Bare `netclaw approvals`
(and `netclaw approvals tui`) SHALL launch an interactive Termina TUI
page. Single-shot subcommands SHALL be `list`, `revoke`, `trust-verb`,
and `help`.

`list` SHALL accept `--audience <personal|team|public>`, `--tool <name>`,
and `--json`. Without flags it SHALL print every audience and tool group
in a stable order. Each entry SHALL be labeled by its scope: entries
with a non-null `directory` print as `<verb> in <directory>`; entries
with `directory: null` print as `<verb> anywhere`. The CLI SHALL NOT
mix verb and directory entries in a single column.

`revoke <pattern>` SHALL remove entries that match `<pattern>`. The
pattern SHALL accept either of the user-visible forms emitted by
`list`: `<verb> in <directory>` matches a `(verb, directory)` entry
exactly, and `<verb> anywhere` matches a `(verb, null)` entry.
Case-sensitivity SHALL match the daemon's matcher comparer (Ordinal on
POSIX, OrdinalIgnoreCase on Windows). `revoke` SHALL accept `--audience`
and `--tool` to scope the removal. `revoke --tool <name> --all` SHALL
clear every entry for that tool in the targeted audiences. `revoke` of
a pattern that does not match any entry SHALL exit non-zero with a
clear message; the CLI SHALL NOT silently succeed.

`trust-verb <verb>` SHALL write a new `(verb, null)` entry for the
specified verb chain — the global wildcard. The subcommand SHALL accept
`--audience <personal|team|public>` (default `personal`) and
`--tool <name>` (default `shell_execute`). `trust-verb` SHALL be the
canonical way to pre-approve a verb for unattended/scheduled invocations
where the cwd will vary across firings. If the entry already exists,
`trust-verb` SHALL exit zero with a "no changes" message.

The CLI SHALL ONLY support adding global wildcards via `trust-verb`. It
SHALL NOT provide a way to add `(verb, directory)` entries from the
CLI; folder-scoped grants SHALL be acquired exclusively through
interactive approval prompts. This is a deliberate friction asymmetry:
prompt-driven grants are the default user path, and the CLI exists to
handle the unattended case and the global-trust case operators
explicitly want.

When the underlying store has quarantined a malformed v1 file
(`tool-approvals.json.v1.bak` sibling), the CLI SHALL emit a one-line
note before list/revoke output indicating the quarantine and pointing
at the backup file. The CLI SHALL NOT silently swallow the condition.

Exit codes SHALL be 0 for success and 1 for user errors (bad flag
combos, unknown audience, no match for revoke, `--all` without `--tool`,
etc.).

#### Scenario: Empty approvals file lists no entries with exit zero

- **GIVEN** `tool-approvals.json` does not exist or contains an empty
  v2 store
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the CLI prints `No persistent approvals.`
- **AND** exits with code `0`

#### Scenario: List filters by audience

- **GIVEN** `tool-approvals.json` contains entries under `personal`
  and `team`
- **WHEN** the operator runs `netclaw approvals list --audience personal`
- **THEN** only the `personal` audience entries are printed

#### Scenario: List labels entries by scope

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"git remote","directory":"/home/user/repos/foo/"}` and
  `{"verb":"freshdesk","directory":null}` under `personal/shell_execute`
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the output includes `git remote in /home/user/repos/foo/`
- **AND** the output includes `freshdesk anywhere`

#### Scenario: List emits typed JSON

- **GIVEN** `tool-approvals.json` contains
  `{"version":2,"audiences":{"personal":{"shell_execute":[
    {"verb":"git push","directory":null}]}}}`
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** the output is valid JSON
- **AND** each entry preserves the `verb`/`directory` shape

#### Scenario: Revoke removes a folder-scoped entry by user-visible form

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"git remote","directory":"/home/user/repos/foo/"}` and
  `{"verb":"freshdesk","directory":null}`
- **WHEN** the operator runs
  `netclaw approvals revoke "git remote in /home/user/repos/foo/"`
- **THEN** the `git remote` entry is removed
- **AND** the `freshdesk anywhere` entry remains
- **AND** the CLI exits with code `0`

#### Scenario: Revoke removes a global wildcard by user-visible form

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"freshdesk","directory":null}`
- **WHEN** the operator runs
  `netclaw approvals revoke "freshdesk anywhere"`
- **THEN** the entry is removed
- **AND** the CLI exits with code `0`

#### Scenario: Revoke with no match exits non-zero

- **GIVEN** `tool-approvals.json` does not contain `git push`
- **WHEN** the operator runs `netclaw approvals revoke "git push anywhere"`
- **THEN** the CLI prints a no-match message
- **AND** exits with code `1`
- **AND** does not modify the file

#### Scenario: trust-verb writes a global wildcard entry

- **GIVEN** `tool-approvals.json` does not yet contain `freshdesk`
- **WHEN** the operator runs `netclaw approvals trust-verb freshdesk`
- **THEN** the file gains entry
  `{"verb":"freshdesk","directory":null}` under
  `personal/shell_execute`
- **AND** the CLI exits with code `0`

#### Scenario: trust-verb is idempotent

- **GIVEN** `tool-approvals.json` already contains
  `{"verb":"freshdesk","directory":null}`
- **WHEN** the operator runs `netclaw approvals trust-verb freshdesk`
- **THEN** the file is unchanged
- **AND** the CLI prints a "no changes" message
- **AND** exits with code `0`

#### Scenario: trust-verb honors --audience and --tool

- **WHEN** the operator runs
  `netclaw approvals trust-verb freshdesk --audience team --tool shell_execute`
- **THEN** the entry is written under `team/shell_execute`
- **AND** the CLI exits with code `0`

#### Scenario: Quarantined v1 file surfaces a one-line note

- **GIVEN** `~/.netclaw/config/tool-approvals.json.v1.bak` exists
  (the daemon has previously quarantined a v1 file)
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the CLI emits a one-line note before the listing pointing
  at the `.v1.bak` file
- **AND** the listing reflects only v2 entries

#### Scenario: Daemon picks up CLI-applied trust-verb without restart

- **GIVEN** the daemon is running
- **WHEN** the operator runs `netclaw approvals trust-verb freshdesk`
- **AND** the agent invokes `freshdesk --since=24h` afterwards
- **THEN** the daemon re-loads the file and observes the new entry
- **AND** the call auto-approves with no prompt
- **AND** the daemon was not restarted

#### Scenario: Bare invocation launches the TUI

- **WHEN** the operator runs `netclaw approvals` with no subcommand
- **THEN** the CLI launches the interactive Termina approvals page
- **AND** the page displays entries grouped by audience and tool with
  scope labels (`<verb> in <dir>` / `<verb> anywhere`)
