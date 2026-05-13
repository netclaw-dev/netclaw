## ADDED Requirements

### Requirement: Safe-verb auto-allow short-circuit in declared safe spaces

The system SHALL maintain a per-OS curated list of demonstrably read-only
verb chains (`safe-verbs.linux.json` and `safe-verbs.windows.json`) shipped
with the daemon and overridable at `~/.netclaw/config/safe-verbs.<os>.json`.
A `ScopedShellSafeVerbPolicy` SHALL evaluate each shell invocation against
the safe-verbs list AND the audience-aware safe-space roots resolved by
`ToolAudienceProfileResolver`. When the candidate verb chain is on the
safe-verbs list AND the candidate's cwd resolves under at least one
safe-space root AND the path contains no symlink segments
(`ContainsSymlinkSegment` returns false), the approval gate SHALL
short-circuit to "approved" with no user prompt. Otherwise the existing
approval gate SHALL apply.

Safe-space roots SHALL be:

- For Personal and Team audiences: `session_dir` (always) plus
  `project_dir` from `WorkingContext` (when set).
- For Public audience: `session_dir` only. Public sessions SHALL NOT
  expand their safe space via `project_dir`, mirroring the read-roots
  restriction `ScopedFileAccessPolicy` enforces for file_read.

The hard-deny list (layer 1) SHALL apply unchanged. The safe-verb
short-circuit SHALL only relax the interactive approval gate (layer 2).
`ToolPathPolicy.CommandReferencesDeniedPath` SHALL still block execution
if a denied path is referenced.

#### Scenario: Read-only verb in project directory auto-runs

- **GIVEN** a Personal session with `project_dir` set to `~/repos/foo/`
- **AND** `grep` is on the Linux safe-verbs list
- **WHEN** the agent invokes `shell_execute` with command
  `grep -r "error" .` and cwd `~/repos/foo/`
- **THEN** the approval gate short-circuits to "approved"
- **AND** no prompt is rendered to the user
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Read-only verb in session directory auto-runs

- **GIVEN** a Personal session with no `project_dir` set
- **AND** `cat` is on the safe-verbs list
- **WHEN** the agent invokes `shell_execute` with command
  `cat inbox/notes.md` and cwd `~/.netclaw/sessions/<id>/`
- **THEN** the approval gate short-circuits to "approved"
- **AND** no prompt is rendered

#### Scenario: Read-only verb outside safe spaces still prompts

- **GIVEN** a Personal session with `project_dir` set to `~/repos/foo/`
- **AND** `grep` is on the safe-verbs list
- **WHEN** the agent invokes `shell_execute` with cwd `/etc/`
- **THEN** the approval gate prompts the user
- **AND** the prompt body shows `/etc/` as the directory header

#### Scenario: Mutating verb in safe space still prompts

- **GIVEN** a Personal session with `project_dir` set to `~/repos/foo/`
- **AND** `git push` is NOT on the safe-verbs list
- **WHEN** the agent invokes `shell_execute` with command
  `git push origin main` and cwd `~/repos/foo/`
- **THEN** the approval gate prompts the user
- **AND** the user can grant `(git push, ~/repos/foo/)` via "Always here"

#### Scenario: Public audience cannot use project_dir as safe space

- **GIVEN** a Public session with `project_dir` set to `~/repos/foo/`
- **AND** `grep` is on the safe-verbs list
- **WHEN** the agent invokes `shell_execute` with cwd `~/repos/foo/`
- **THEN** the approval gate prompts the user
- **AND** Public's only safe space remains `session_dir`

#### Scenario: Symlink under safe-space root cannot extend safe scope

- **GIVEN** a Personal session with `project_dir` set to `~/repos/foo/`
- **AND** `~/repos/foo/leak` is a symlink resolving to `/etc`
- **WHEN** the agent invokes `shell_execute` with cwd `~/repos/foo/leak/`
  and command `cat passwd`
- **THEN** the safe-verb short-circuit SHALL NOT apply
  (`ContainsSymlinkSegment` returns true)
- **AND** the approval gate prompts the user (or `ToolPathPolicy`
  hard-denies if the resolved path is protected)

#### Scenario: User-overridden safe-verbs file extends defaults

- **GIVEN** the user has written
  `~/.netclaw/config/safe-verbs.linux.json` containing the verb `eza`
- **WHEN** the daemon loads safe-verbs configuration
- **THEN** `eza` is treated as a safe verb in addition to the shipped defaults
- **AND** `eza` invocations in safe spaces auto-run without prompting

### Requirement: Five-button approval prompt with verb-and-directory framing

When the approval gate prompts the user, the prompt SHALL render five
buttons in one row: `Once`, `This chat`, `Always here`, `Always anywhere`,
`Deny`. The buttons `Always anywhere` and `Deny` SHALL be styled as
danger (Slack `style: "danger"`, Discord `ButtonStyle.Danger`). All
button labels SHALL fit within Slack's 76-character and Discord's
80-character button-text caps.

The prompt body SHALL show the cwd in the header
(`Approve in <cwd> ?`) and the extracted verb chains as a bulleted list.
Single-verb commands MAY collapse the list into the header
(`Approve <verb> in <cwd> ?`). The body SHALL NOT render separate
"Patterns" or "Directory Roots" sections.

Button semantics:

- `Once` SHALL run the command this one time and persist nothing.
- `This chat` SHALL allow the extracted verbs in the prompt's directory
  for the rest of the session, stored in session-scoped memory only.
- `Always here` SHALL persist `(verb, prompt's directory)` entries to
  `tool-approvals.json` for each extracted verb.
- `Always anywhere` SHALL persist `(verb, null)` entries for each
  extracted verb — the global wildcard.
- `Deny` SHALL refuse this call only. Denying a verb SHALL NOT ban it
  for future invocations.

#### Scenario: Compound command shows verbs as bullets

- **GIVEN** the agent invokes `shell_execute` with command
  `cd ~/repos/foo && git remote -v && git rev-parse HEAD`
  and cwd `~/repos/foo/`
- **WHEN** the approval prompt is rendered on Slack
- **THEN** the body header reads `Approve in ~/repos/foo/ ?`
- **AND** the verbs `cd`, `git remote`, `git rev-parse` appear as bullets
- **AND** the action row contains five buttons
- **AND** `Always anywhere` and `Deny` are styled as danger

#### Scenario: Always here persists folder-scoped entries

- **GIVEN** an approval prompt for verbs `git remote`, `git rev-parse`
  in cwd `~/repos/foo/`
- **WHEN** the user clicks `Always here`
- **THEN** `tool-approvals.json` gains entries
  `{"verb": "git remote", "directory": "~/repos/foo/"}` and
  `{"verb": "git rev-parse", "directory": "~/repos/foo/"}`
- **AND** the resolution message reads
  `Saved: git remote, git rev-parse in ~/repos/foo/`

#### Scenario: Always anywhere persists global entries

- **GIVEN** an approval prompt for verb `freshdesk` in cwd `~/.netclaw/sessions/<id>/`
- **WHEN** the user clicks `Always anywhere`
- **THEN** `tool-approvals.json` gains entry
  `{"verb": "freshdesk", "directory": null}`
- **AND** the resolution message reads `Saved: freshdesk anywhere`

#### Scenario: This chat persists session-scoped only

- **GIVEN** an approval prompt for verb `jsonlint` in cwd `~/repos/foo/`
- **WHEN** the user clicks `This chat`
- **THEN** session-scoped memory records `(jsonlint, ~/repos/foo/)`
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session prompts again

#### Scenario: Deny refuses only the current call

- **GIVEN** an approval prompt for verb `git push`
- **WHEN** the user clicks `Deny`
- **THEN** the current call is refused
- **AND** `tool-approvals.json` is NOT modified
- **AND** a later `git push` call still prompts

### Requirement: Resolution message single-line format

After an approval response is processed, the channel SHALL render a
single-line resolution message replacing today's separate `Patterns` and
`Directory Roots` sections. The line SHALL identify the verbs and the
scope. Permitted formats:

- `Saved: <verb-list> in <directory>` — for `Always here`.
- `Saved: <verb-list> anywhere` — for `Always anywhere`.
- `Saved for this chat: <verb-list> in <directory>` — for `This chat`.
- `Approved (no save)` — for `Once`.
- `Denied` — for `Deny`.

#### Scenario: Resolution shows folder scope for Always here

- **GIVEN** the user has clicked `Always here` for verbs
  `jsonlint, git pull` in `~/repos/foo/`
- **WHEN** the resolution message is rendered
- **THEN** the message reads `Saved: jsonlint, git pull in ~/repos/foo/`
- **AND** no `Patterns` or `Directory Roots` headers are emitted

#### Scenario: Resolution shows global scope for Always anywhere

- **GIVEN** the user has clicked `Always anywhere` for verb `freshdesk`
- **WHEN** the resolution message is rendered
- **THEN** the message reads `Saved: freshdesk anywhere`

### Requirement: Pattern extraction refuses bash control-flow

`ShellTokenizer.SplitCompoundCommand` SHALL detect bash control-flow
tokens (`for`, `while`, `do`, `done`, `then`, `fi`, `case`, `esac`) and
unbalanced quotes/brackets. When detected, the tokenizer SHALL return an
empty verb-chain list. The approval gate SHALL respond by offering only
the `Once` and `Deny` buttons (no `This chat`, `Always here`, or
`Always anywhere`) and the prompt body SHALL show a hint: "complex
command — only one-shot approval available". No persistent grant SHALL
be possible for unparseable commands.

#### Scenario: For-loop produces empty verb-chain list

- **GIVEN** the command
  `for pid in $(pgrep netclawd); do echo "$pid"; done`
- **WHEN** `ShellTokenizer.SplitCompoundCommand` runs
- **THEN** the returned verb-chain list is empty

#### Scenario: Approval prompt for messy command offers only Once and Deny

- **GIVEN** the agent invokes `shell_execute` with the for-loop above
  and cwd outside any safe space
- **WHEN** the approval prompt is rendered
- **THEN** only `Once` and `Deny` buttons are present
- **AND** the body shows the "complex command" hint

#### Scenario: Unbalanced quotes treated as messy

- **GIVEN** the command `echo "unterminated`
- **WHEN** the tokenizer runs
- **THEN** the verb-chain list is empty
- **AND** the approval gate offers only `Once` and `Deny`

## MODIFIED Requirements

### Requirement: Persistent approval storage

The system SHALL store persistent approvals in
`~/.netclaw/config/tool-approvals.json` using a `version: 2` typed
schema. Each entry SHALL be an `ApprovalEntry` with a required `verb`
field (the verb chain, e.g. `git remote`) and an optional `directory`
field (an absolute path, or `null` for the global wildcard). The file
SHALL contain per-audience sections with per-tool `ApprovalEntry` lists.
The file SHALL NOT be monitored by `ConfigWatcherService`.

When the daemon reads a `tool-approvals.json` file that does not have
`version: 2`, the file SHALL be quarantined to
`tool-approvals.json.v1.bak` and an empty v2 store SHALL be returned.
The daemon SHALL write the empty v2 store on the next persist call. No
automatic translation of v1 entries SHALL be performed.

The matcher SHALL approve a candidate invocation when there exists an
`ApprovalEntry` whose `verb` equals the candidate's extracted verb
chain AND (`directory` is `null` OR the candidate's cwd is under
`directory`).

The file SHALL also be operator-editable via the `netclaw approvals`
CLI (see the `netclaw-cli` capability). The daemon SHALL pick up
out-of-band edits — whether made by direct file editing or by the
CLI — on the next approval check, without requiring a restart.

#### Scenario: Always here persists typed (verb, directory) entries

- **GIVEN** the user clicks `Always here` for verbs `git remote` and
  `git rev-parse` in cwd `~/repos/foo/`
- **WHEN** the approval is processed
- **THEN** `tool-approvals.json` contains
  `[{"verb":"git remote","directory":"~/repos/foo/"},
    {"verb":"git rev-parse","directory":"~/repos/foo/"}]`
- **AND** the daemon does NOT restart

#### Scenario: Always anywhere persists null-directory entry

- **GIVEN** the user clicks `Always anywhere` for verb `freshdesk`
- **WHEN** the approval is processed
- **THEN** `tool-approvals.json` contains
  `{"verb":"freshdesk","directory":null}`

#### Scenario: v1 file quarantined on first read

- **GIVEN** `tool-approvals.json` exists without a `version` field
  (or with `version` other than `2`)
- **WHEN** the daemon loads the file
- **THEN** the file is moved to `tool-approvals.json.v1.bak`
- **AND** `Load()` returns an empty v2 store
- **AND** no v1 entries are translated to v2

#### Scenario: Matcher approves under directory entry

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"git remote","directory":"~/repos/foo/"}`
- **WHEN** the agent invokes `git remote -v` with cwd `~/repos/foo/`
- **THEN** the matcher returns approved
- **AND** no prompt is rendered

#### Scenario: Matcher approves under null-directory entry

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"freshdesk","directory":null}`
- **WHEN** the agent invokes `freshdesk --since=24h` with cwd
  `~/.netclaw/sessions/<id>/`
- **THEN** the matcher returns approved regardless of cwd

#### Scenario: Matcher rejects when cwd is outside entry directory

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"git remote","directory":"~/repos/foo/"}`
- **WHEN** the agent invokes `git remote -v` with cwd `~/repos/bar/`
- **THEN** the matcher returns not-approved
- **AND** the approval gate prompts the user

#### Scenario: Approve once is retry-scoped only

- **GIVEN** the user clicks `Once` for command `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Operator-applied revocation visible without restart

- **GIVEN** the daemon is running with a persisted entry
  `{"verb":"git push","directory":null}`
- **WHEN** an operator removes that entry via `netclaw approvals revoke`
- **AND** a new approval check evaluates `git push`
- **THEN** the daemon re-loads the file and observes the entry is gone
- **AND** the user is prompted for approval again
- **AND** the daemon was not restarted

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands
using tokenization. The verb chain SHALL consist of non-flag tokens from
the start of the command until the first flag (`-`), path, or URL
argument. For shell approval units, `&&`, `||`, and `;` SHALL split into
separate units, while `|` SHALL remain inside the current unit. For
`bash -c` or `sh -c` wrappers, the inner command SHALL be extracted and
scanned recursively.

When `ShellTokenizer.SplitCompoundCommand` detects bash control-flow
tokens or unbalanced quotes/brackets, it SHALL return an empty
verb-chain list. The approval gate SHALL then offer only `Once` and
`Deny`. See the "Pattern extraction refuses bash control-flow"
requirement for details.

The matcher SHALL operate on `ApprovalEntry` records keyed by
`(verb, directory)`. The "is this string a verb chain or a directory
root?" inspection logic of v1 SHALL NOT be present in the v2 matcher.

Approval persistence SHALL store one `ApprovalEntry` per extracted verb
chain. Compound commands SHALL produce N entries from one user click on
`Always here` or `Always anywhere`.

#### Scenario: Verb chain extracted from simple command

- **GIVEN** the command `git push origin main`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `git push`

#### Scenario: Verb chain stops at flag

- **GIVEN** the command `ls -la /tmp`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `ls`
- **AND** the flag and path are not part of the persisted verb chain

#### Scenario: Multi-level verb chain

- **GIVEN** the command `docker compose up -d`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `docker compose up`

#### Scenario: Control operators create separate approval units

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** `git add`, `git commit`, and `git push` are checked as
  separate approval units against the v2 matcher

#### Scenario: Compound segments batched in one prompt

- **GIVEN** none of `git add`, `git commit`, `git push` are approved
- **WHEN** the command `git add . && git commit -m "fix" && git push`
  is checked
- **THEN** a single approval prompt lists all three verbs as bullets
- **AND** one click on `Always here` persists three `(verb, cwd)` entries

#### Scenario: bash -c inner command scanned recursively

- **GIVEN** the command `bash -c "git push --force"`
- **WHEN** approval and hard deny are checked
- **THEN** the inner command `git push --force` is extracted and scanned
- **AND** verb chain `git push` is checked through the v2 matcher

### Requirement: Directory-root approvals for shell_execute

For `shell_execute`, persistent approvals SHALL be stored as typed
`(verb, directory)` `ApprovalEntry` records, NOT as separate verb
patterns and directory-root entries. The matcher SHALL approve a
candidate invocation when an `ApprovalEntry` exists whose `verb` matches
the candidate's verb chain AND (`directory` is `null` OR the candidate's
cwd is under `directory`).

`Once` SHALL retry only the blocked call; it SHALL NOT create any
session or persistent approval.

`This chat` SHALL store `(verb, prompt's directory)` entries in
session-scoped memory only.

`Always here` SHALL persist `(verb, prompt's directory)` entries to
`tool-approvals.json`.

`Always anywhere` SHALL persist `(verb, null)` entries to
`tool-approvals.json` — the global wildcard.

The system SHALL enforce path normalization, boundary-safe containment,
path traversal checks, and `ToolPathPolicy` as the safety backstop.
`ToolPathPolicy` SHALL resolve symlinks along every component of a
candidate path so that a planted symlink under an approved directory
cannot be used to reach a protected path that lies outside that
directory.

The minimum-depth check from v1 (rejecting roots like `/` or `/etc/`)
SHALL still apply to the directory portion of `(verb, directory)`
entries: `Always here` SHALL NOT persist a directory shallower than
two path segments. When the prompt's directory is too shallow, the
prompt SHALL omit the `Always here` button (only `Once`, `This chat`,
`Always anywhere`, `Deny` remain), so the user cannot accidentally
write a too-shallow root.

#### Scenario: Once retries only the blocked call

- **GIVEN** a shell command `cat ~/repos/foo/notes.md` requires approval
- **WHEN** the user clicks `Once`
- **THEN** only the current blocked call is retried
- **AND** no `ApprovalEntry` is recorded
- **AND** a later `cat ~/repos/foo/other.md` prompts again

#### Scenario: Always here stores (verb, directory) entry

- **GIVEN** a shell command `grep -l "timeout" daemon.log` with cwd
  `~/.netclaw/logs/`
- **WHEN** the user clicks `Always here`
- **THEN** `{"verb":"grep","directory":"~/.netclaw/logs/"}` is written
  to `tool-approvals.json`
- **AND** a future `wc -l app.log` with cwd `~/.netclaw/logs/` does NOT
  match this entry (different verb)
- **AND** a future `grep "info" archive.log` with cwd
  `~/.netclaw/logs/` is auto-approved (same verb, same directory)

#### Scenario: Always anywhere stores (verb, null) entry

- **GIVEN** a shell command `freshdesk --since=24h` requires approval
- **WHEN** the user clicks `Always anywhere`
- **THEN** `{"verb":"freshdesk","directory":null}` is written to
  `tool-approvals.json`
- **AND** a scheduled task firing `freshdesk` in any cwd is
  auto-approved on next invocation

#### Scenario: Boundary-safe matching prevents prefix collisions

- **GIVEN** `{"verb":"cat","directory":"/home/user/"}` is approved
- **WHEN** the agent runs `cat data.txt` with cwd `/home/usersecret/`
- **THEN** the candidate does NOT match the entry
- **AND** the approval gate prompts the user

#### Scenario: Symlink in cwd breaks the approval match

- **GIVEN** `{"verb":"cat","directory":"/home/user/safe/"}` is approved
- **AND** `/home/user/safe/leak` is a directory symlink resolving
  to `/etc`
- **WHEN** the agent runs `cat passwd` with cwd `/home/user/safe/leak/`
- **THEN** the symlink-segment check breaks the auto-approval
- **AND** `ToolPathPolicy.CommandReferencesDeniedPath` blocks execution
  if the canonical path is protected

#### Scenario: Shallow directory prevents Always here

- **GIVEN** an approval prompt for `cat /etc/passwd` (cwd `/etc/`)
- **WHEN** the prompt is rendered
- **THEN** the `Always here` button is omitted
- **AND** only `Once`, `This chat`, `Always anywhere`, `Deny` are shown

## REMOVED Requirements

### Requirement: Directory root extraction via IToolApprovalMatcher

**Reason:** Replaced by the typed `(verb, directory)` `ApprovalEntry`
model. Directory roots are no longer a separate matcher concept; they
are the `directory` field on every entry. `IToolApprovalMatcher`
collapses to verb-chain extraction; the cwd providing the directory
half of the pair comes from `ToolExecutionContext`.

**Migration:** None — breaking change. Implementation removes
`ExtractDirectoryRoots()` from `IToolApprovalMatcher` and the
corresponding implementations in `ShellApprovalMatcher`,
`DefaultApprovalMatcher`, and `FilePathApprovalMatcher`. Pattern
extraction returns verb chains; the approval gate threads the cwd
through `ToolInteractionRequest.Cwd`.

### Requirement: Dynamic approval option labels

**Reason:** PR #937 already reverted dynamic labels to fixed labels to
fit Slack/Discord button caps. The v2 prompt design replaces the
3-button + dynamic-label approach with 5 fixed-label buttons
(`Once`, `This chat`, `Always here`, `Always anywhere`, `Deny`). The
verb-and-directory framing now lives in the prompt body header
(`Approve in <cwd> ?`) and the verb bullet list, not in button text.

**Migration:** None — breaking change. The `Always here` and
`Always anywhere` buttons replace the directory-root-aware label
behavior; the cwd is shown in the prompt body, not the button.
