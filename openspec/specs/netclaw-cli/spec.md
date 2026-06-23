## Purpose

Define operator-facing CLI surface area for Netclaw: the `netclaw init` wizard,
the `netclaw doctor` diagnostic, the `netclaw config` settings surface, and the
`netclaw approvals` command for managing persistent tool approvals.
## Requirements
### Requirement: Config command surface

The CLI SHALL expose `netclaw config` as a top-level command. The command
SHALL operate on local config files and SHALL behave per the
`netclaw-config-command` capability.

If no config exists, `netclaw config` SHALL print a plain message directing
the operator to `netclaw init` and exit non-zero without launching Termina.

#### Scenario: Help text describes config as post-install settings surface

- **WHEN** the operator runs `netclaw config --help`
- **THEN** the command exits zero
- **AND** help text describes `netclaw config` as the main post-install
  settings surface
- **AND** help text references `netclaw init` as the bootstrap companion

#### Scenario: No-args invocation launches dashboard on configured install

- **GIVEN** `netclaw.json` exists
- **WHEN** the operator runs `netclaw config`
- **THEN** the domain-oriented dashboard launches

#### Scenario: Missing install refuses with plain message

- **GIVEN** `netclaw.json` does not exist
- **WHEN** the operator runs `netclaw config`
- **THEN** stderr contains ``No configuration found. Run `netclaw init` first.``
- **AND** the command exits non-zero
- **AND** no partial TUI starts

### Requirement: Personal shell approval defaults are explicit

When bootstrap selects `Personal` posture, the written config SHALL make the
recommended shell approval default explicit by writing
`Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`
rather than relying on runtime-only implicit defaults.

#### Scenario: Personal bootstrap writes explicit shell approval default

- **GIVEN** the operator completes `netclaw init` with `Personal` posture
- **WHEN** the wizard writes the config
- **THEN** `netclaw.json` includes
  `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`

### Requirement: Doctor checks for approval configuration

`netclaw doctor` SHALL validate approval configuration consistency. It SHALL
warn when the Personal audience enables host shell access without an explicit
`shell_execute` approval gate in `ApprovalPolicy.ToolOverrides`. It SHALL warn
when `tool-approvals.json` contains patterns for audiences or tools that are no
longer configured.

#### Scenario: Doctor warns about Personal host shell without explicit approval gate

- **GIVEN** the Personal audience has host shell access enabled
- **AND** `ApprovalPolicy.ToolOverrides` does not contain `shell_execute`
- **WHEN** `netclaw doctor` runs
- **THEN** it emits a warning that Personal host shell is enabled without an
  explicit `shell_execute` approval gate
- **AND** the warning recommends running `netclaw init` again or setting
  `Tools.AudienceProfiles.Personal.ApprovalPolicy.ToolOverrides.shell_execute = "approval"`

#### Scenario: Doctor warns about stale approval patterns

- **GIVEN** `tool-approvals.json` has patterns for `team.shell_execute`
- **AND** the Team audience has shell mode Off
- **WHEN** `netclaw doctor` runs
- **THEN** it emits an info advisory: "Persistent approvals exist for
  team.shell_execute but shell is disabled for Team audience."

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

### Requirement: Approval surfaces show grant creation time

The `netclaw approvals` inspection surfaces SHALL display when each
persisted grant was created. Both the `netclaw approvals list` command
and the interactive `netclaw approvals` Termina TUI list SHALL derive
this from the `ApprovalEntry.createdAt` field.

Human-readable output (the default `list` rendering and the TUI list
rows) SHALL render the creation time as relative text — for example
`added 3 days ago`. An entry whose `createdAt` is `null` (a grant
written before timestamp tracking) SHALL render a stable placeholder
(`added —`) rather than a fabricated or omitted value. The creation-time
text SHALL NOT be mixed into the scope label column; it is presented as
distinct per-entry metadata.

`netclaw approvals list --json` SHALL expose the raw `createdAt` value
on each entry — an ISO-8601 string when present, `null` otherwise — so
scripts can compare it against daemon log timestamps. The JSON output
SHALL remain a superset of the previous shape: existing `verb` and
`directory` fields are unchanged.

#### Scenario: List shows relative creation time

- **GIVEN** `tool-approvals.json` contains an entry whose `createdAt` is
  three days before now
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the entry's row includes relative text such as `added 3 days ago`

#### Scenario: Entry without a timestamp shows a placeholder

- **GIVEN** `tool-approvals.json` contains an entry with no `createdAt`
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the entry's row shows the `added —` placeholder
- **AND** the command exits with code `0`

#### Scenario: JSON output exposes the raw timestamp

- **GIVEN** `tool-approvals.json` contains one entry with a `createdAt`
  and one without
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** the first entry's JSON object includes a `createdAt`
  ISO-8601 string
- **AND** the second entry's `createdAt` is `null`

#### Scenario: TUI list shows creation time per entry

- **GIVEN** the operator launches the interactive `netclaw approvals` TUI
- **AND** `tool-approvals.json` contains at least one timestamped entry
- **WHEN** the approvals list page renders
- **THEN** each row shows the grant's relative creation time alongside
  its scope label
### Requirement: CLI derives local control-plane endpoint from daemon bind config

When no explicit daemon endpoint override exists, the CLI SHALL derive a usable local control-plane endpoint from `Daemon.Host` and `Daemon.Port` in daemon configuration instead of always falling back to `http://127.0.0.1:5199`.

If the daemon bind host is an unspecified wildcard listen address such as `0.0.0.0`, `::`, or `[::]`, the CLI SHALL normalize it to a connectable loopback host for local control-plane use.

#### Scenario: Explicit environment override still wins

- **GIVEN** `NETCLAW_DAEMON_ENDPOINT` is set
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it uses the environment override

#### Scenario: Client config override wins over daemon bind fallback

- **GIVEN** no environment override is set
- **AND** the client config file contains a daemon endpoint
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it uses the client config endpoint

#### Scenario: Daemon bind config provides fallback endpoint

- **GIVEN** no environment override or client endpoint override exists
- **AND** daemon config contains `Host = "10.0.0.20"` and `Port = 6200`
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it returns `http://10.0.0.20:6200`

#### Scenario: Wildcard bind is normalized for local control-plane use

- **GIVEN** no environment override or client endpoint override exists
- **AND** daemon config contains `Host = "0.0.0.0"` and `Port = 5199`
- **WHEN** the CLI resolves the daemon endpoint
- **THEN** it returns `http://127.0.0.1:5199`

### Requirement: Daemon-host CLI auth decision uses effective exposure requirements

The daemon-host CLI SHALL decide whether to attach a bearer token based on whether the resolved endpoint requires remote authentication, not only on whether the endpoint host is loopback.

#### Scenario: Reverse-proxy loopback control-plane endpoint attaches token

- **GIVEN** the resolved endpoint is `http://127.0.0.1:5199`
- **AND** daemon config exposure mode is `reverse-proxy`
- **AND** a device token exists locally
- **WHEN** the CLI builds its daemon connection
- **THEN** it attaches the bearer token

#### Scenario: Local-mode loopback control-plane endpoint skips token

- **GIVEN** the resolved endpoint is `http://127.0.0.1:5199`
- **AND** daemon config exposure mode is `local`
- **WHEN** the CLI builds its daemon connection
- **THEN** it does not attach a bearer token by default
