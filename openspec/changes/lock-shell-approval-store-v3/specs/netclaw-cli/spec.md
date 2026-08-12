## MODIFIED Requirements

### Requirement: Operator CLI for persistent tool approvals

The CLI SHALL provide `netclaw approvals` for the persistent approval file.
It SHALL use `ToolApprovalStore` without a daemon connection. Bare
`netclaw approvals` and `netclaw approvals tui` SHALL open the Termina page.
The other commands SHALL be `list`, `revoke`, `trust-verb`, and `help`.

`list` SHALL accept `--audience`, `--tool`, and `--json`. Text output SHALL use
a stable order. A shell row SHALL show shell, match kind, canonical phrase,
scope, and age. A non-shell row SHALL show its exact phrase, scope, and age.

The shell scope label SHALL use these forms:

- `<shell> token-prefix "<phrase>" anywhere`
- `<shell> token-prefix "<phrase>" in <directory>`
- `<shell> legacy-exact "<phrase>" anywhere`
- `<shell> legacy-exact "<phrase>" in <directory>`

A non-shell entry SHALL use `NonShell exact "<phrase>" anywhere` or
`NonShell exact "<phrase>" in <directory>`. The JSON-quoted phrase SHALL keep
scope separator text unambiguous. `revoke` SHALL accept the prior untyped label
when that label selects one entry.
`--json` SHALL emit the exact version-3 entry form and audience and tool keys.

`revoke <pattern>` SHALL accept each text form from `list`. It SHALL compare
shell, match kind, phrase, and scope. It SHALL use native shell case rules. It
SHALL accept the old untyped scope form only when that form selects one entry.
An ambiguous old form SHALL fail with no file change.

`revoke` SHALL accept `--audience` and `--tool`. The form
`revoke --tool <name> --all` SHALL remove all entries for that tool. No match
SHALL exit with code 1 and a clear message.

`trust-verb <phrase>` SHALL accept `--audience`, `--tool`, and `--shell`. The
default audience SHALL be `personal`. The default tool SHALL be
`shell_execute`. For `shell_execute`, the default shell SHALL be Bash on POSIX
and PowerShell on Windows. `--shell bash|powershell` SHALL select the canonical
ShellSyntaxTree parser. The standalone CLI SHALL try the PowerShell 7 and
Windows PowerShell 5.1 parsers. It SHALL use a valid PowerShell 7 result first.
It SHALL use a valid Windows PowerShell 5.1 result only when the preferred
parser rejects the phrase. The daemon SHALL continue to use its resolved
native dialect for execution-time command facts. `--shell` with another tool
SHALL be a user error.

For `shell_execute`, `trust-verb` SHALL accept one complete static command
phrase. It SHALL use ShellSyntaxTree canonical verb tokens and write a global
`TokenPrefix` entry. It SHALL reject compound, dynamic, or incomplete input.
It SHALL not use a private command parser or a whitespace split.

The occurrence SHALL have no parser-classified argument, flag, assignment,
redirect, cwd effect, substitution, or control-flow effect. The input SHALL
equal the canonical token phrase. The CLI SHALL fail instead of a silent phrase
reduction. It SHALL not reinterpret a parser-classified verb token through
executable-private grammar.

For another tool, `trust-verb` SHALL write the compatible global non-shell
exact entry. It SHALL keep support for an arbitrary `--tool` value. It SHALL
not add shell members.

The CLI SHALL add only global entries. A folder entry SHALL come from an
interactive approval. An equal entry SHALL exit with code 0 and a no-change
message.

If the store is unavailable, each command SHALL exit with code 1. It SHALL
show one bounded error and SHALL not change the active file. If `.v2.bak`
exists, text `list` output and `help` SHALL state the manual recovery steps.
The note SHALL tell the operator to stop the daemon and restore the backup for
the current daemon. `list --json` SHALL remain exact JSON with no prose. The
old v1 quarantine note SHALL remain for `.v1.bak`.

Exit code 0 SHALL mean success. Exit code 1 SHALL mean a user or store error.

#### Scenario: Empty store lists no entries

- **GIVEN** the approval file is absent or has an empty version-3 store
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the CLI prints `No persistent approvals.`
- **AND** it exits with code 0

#### Scenario: List filters by audience

- **GIVEN** the store has `personal` and `team` entries
- **WHEN** the operator uses `list --audience personal`
- **THEN** the output has only `personal` entries

#### Scenario: List labels typed shell phrases

- **GIVEN** a Bash token-prefix grant covers `git push` in `/work/repo`
- **AND** a Bash legacy grant covers `git push` everywhere
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** output has `Bash token-prefix "git push" in /work/repo`
- **AND** output has `Bash legacy-exact "git push" anywhere`

#### Scenario: List JSON uses version 3 forms

- **GIVEN** the store has token-prefix, legacy, and non-shell entries
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** output is valid JSON
- **AND** each entry has its exact version-3 form

#### Scenario: Revoke removes one typed phrase

- **GIVEN** token-prefix and legacy grants have phrase `git push`
- **WHEN** the operator revokes `Bash token-prefix "git push" anywhere`
- **THEN** the token-prefix grant is absent
- **AND** the legacy grant remains

#### Scenario: Old ambiguous revoke form fails

- **GIVEN** two typed entries render the old form `git push anywhere`
- **WHEN** the operator uses that old form with `revoke`
- **THEN** the command exits with code 1
- **AND** the store does not change

#### Scenario: Revoke with no match fails

- **GIVEN** no store entry matches the pattern
- **WHEN** the operator runs `netclaw approvals revoke <pattern>`
- **THEN** the command exits with code 1
- **AND** the store does not change

#### Scenario: Static Bash phrase creates token prefix

- **WHEN** the operator trusts `git push` for Bash `shell_execute`
- **THEN** the store adds Bash tokens `git`, `push`
- **AND** the entry has global scope

#### Scenario: Native Windows default prefers PowerShell 7 grammar

- **GIVEN** Netclaw runs natively on Windows
- **WHEN** the operator omits `--shell` for `shell_execute`
- **THEN** the CLI uses a valid PowerShell 7 grant result first
- **AND** it uses the Windows PowerShell 5.1 result only when the first result is invalid
- **AND** it stores shell `PowerShell`

#### Scenario: Compound shell phrase fails

- **WHEN** the operator trusts `git status; rm file` for `shell_execute`
- **THEN** the command exits with code 1
- **AND** the store does not change

#### Scenario: Shell effects fail phrase creation

- **WHEN** the operator trusts a phrase with a flag, parser-classified
  argument, assignment, or redirect
- **THEN** the command exits with code 1
- **AND** no reduced token-prefix entry is stored

#### Scenario: Parser-owned phrase keeps every token

- **WHEN** the operator trusts `git push origin`
- **AND** ShellSyntaxTree returns canonical tokens `git`, `push`, and `origin`
- **THEN** the store adds all three tokens
- **AND** no `git push` entry is added

#### Scenario: Non-shell tool stays exact

- **WHEN** the operator trusts `create-page` for a non-shell tool
- **THEN** the store adds a non-shell exact entry
- **AND** the entry has no shell member

#### Scenario: trust-verb is idempotent

- **GIVEN** the exact target entry is in the store
- **WHEN** the operator issues the same trust command
- **THEN** the file does not change
- **AND** the command exits with code 0

#### Scenario: Store error fails closed

- **GIVEN** the store has an invalid version-3 entry
- **WHEN** the operator runs list, revoke, or trust-verb
- **THEN** the command exits with code 1
- **AND** the active file stays byte-identical

#### Scenario: Backup note states current recovery path

- **GIVEN** `.v2.bak` exists
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the CLI states how to stop the daemon and restore the backup
- **AND** it does not claim that an old binary can read version 3

#### Scenario: Daemon sees a CLI change without restart

- **GIVEN** the daemon is active
- **WHEN** the CLI adds or revokes a grant
- **THEN** the next actor snapshot sees the new version-3 state

#### Scenario: Bare command opens the TUI

- **WHEN** the operator runs `netclaw approvals`
- **THEN** the CLI opens the Termina approvals page
- **AND** each row uses the typed phrase and scope label

### Requirement: Approval surfaces show grant creation time

The approval CLI and TUI SHALL show `createdAt` for each entry. Text output
SHALL use relative text, such as `added 3 days ago`. A null value SHALL show
`added —`. The age SHALL stay separate from the scope label.

`list --json` SHALL emit the raw `createdAt` member. The member SHALL contain an
ISO-8601 value or JSON null. It SHALL appear with the exact version-3 entry
form. A token-prefix entry does not need a `verb` member.

#### Scenario: List shows relative creation time

- **GIVEN** an entry has a timestamp from three days before now
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the row includes `added 3 days ago`

#### Scenario: Null timestamp shows a placeholder

- **GIVEN** an entry has null `createdAt`
- **WHEN** the operator runs `netclaw approvals list`
- **THEN** the row shows `added —`

#### Scenario: JSON output shows raw time

- **GIVEN** one entry has a timestamp and one has null
- **WHEN** the operator runs `netclaw approvals list --json`
- **THEN** the first entry has an ISO-8601 `createdAt`
- **AND** the second entry has JSON null

#### Scenario: TUI shows time for each row

- **GIVEN** the store has a timestamped entry
- **WHEN** the approvals page appears
- **THEN** the row shows its relative age and typed scope
