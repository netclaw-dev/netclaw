## Purpose

Define how the daemon gates tool execution behind interactive user approval.
Covers per-audience policy configuration, hard deny rules, command pattern
matching, the `IToolApprovalMatcher` extension point, mid-turn approval pauses,
the channel-mediated `ToolInteractionRequest`/`ToolInteractionResponse`
protocol, persistent approval storage in `~/.netclaw/config/tool-approvals.json`,
directory-scoped shell approvals (root-based, verb-agnostic), and the
`ToolPathPolicy` symlink-resolving safety backstop that ensures broader
directory grants never widen access to protected paths.
## Requirements
### Requirement: Tool approval configuration per audience

The system SHALL support per-audience tool approval configuration via
`ToolApprovalConfig` on `ToolAudienceProfile`. Each audience profile SHALL
independently specify a `DefaultMode` (Auto, Approval, Deny) and per-tool
overrides in `ToolOverrides`. The default `DefaultMode` SHALL be `Auto` (no
approval required). Runtime audience defaults SHALL NOT implicitly place
`shell_execute` in `Approval` mode. Instead, the init-generated Personal config
SHALL explicitly write
`ApprovalPolicy.ToolOverrides.shell_execute = Approval` as the recommended
shell-safe default.

#### Scenario: Shell requires approval in init-generated Personal config

- **GIVEN** a Personal audience session whose generated config explicitly sets
  `ApprovalPolicy.ToolOverrides.shell_execute` to `Approval`
- **WHEN** the agent invokes `shell_execute`
- **THEN** `ToolAccessPolicy` marks the call as approval-gated
- **AND** `DispatchingToolExecutor` consults `IToolApprovalService` before execution
- **AND** if the command pattern is not approved, an approval prompt is emitted

#### Scenario: Tool in Auto mode executes without approval

- **GIVEN** a tool whose approval mode is `Auto` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool executes immediately without an approval prompt

#### Scenario: Tool in Deny mode is always blocked

- **GIVEN** a tool whose approval mode is `Deny` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool is denied with reason `tool_denied_by_approval_policy`
- **AND** no approval prompt is offered

#### Scenario: Per-audience independence

- **GIVEN** Personal sets `shell_execute` to `Approval` and Team sets it to `Deny`
- **WHEN** a Personal session invokes `shell_execute`
- **THEN** `ToolAccessPolicy` marks the call as approval-gated
- **AND** `DispatchingToolExecutor` may prompt if `IToolApprovalService` reports unapproved patterns
- **AND** when a Team session invokes `shell_execute`
- **THEN** the system denies immediately without prompting

### Requirement: Configurable hard deny list

The system SHALL enforce a configurable hard deny list of command patterns that
are blocked before the approval gate is consulted. Denied commands SHALL never
be approvable. The system SHALL ship with sensible defaults: commands that kill
the Netclaw daemon process, `rm -rf /`, `rm -rf ~/`, and fork bombs. Operators
SHALL be able to add or remove patterns via configuration.

#### Scenario: Hard-denied command blocked before approval

- **GIVEN** a command matching the hard deny list (e.g., `netclaw daemon stop`)
- **WHEN** the agent invokes `shell_execute` with that command
- **THEN** the command is denied with reason `hard_deny_self_destructive`
- **AND** no approval prompt is offered
- **AND** the denial is logged

#### Scenario: Hard deny enforced even in HostAllowed mode

- **GIVEN** `ShellMode` is `HostAllowed` (no approval config)
- **WHEN** the agent runs a hard-denied command
- **THEN** the command is still blocked

#### Scenario: Operator adds custom hard deny pattern

- **GIVEN** the operator adds `docker rm` to the hard deny list in config
- **WHEN** the agent runs `docker rm my-container`
- **THEN** the command is denied

#### Scenario: Compound command with hard-denied segment

- **GIVEN** a compound command `git add . && netclaw daemon stop`
- **WHEN** the agent invokes `shell_execute`
- **THEN** the entire command is denied because one segment matches hard deny

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands
using tokenization. The verb chain SHALL consist of non-flag tokens from
the start of the command until the first flag (`-`), path, or URL
argument. Extraction is greedy: bare-word operands that are neither flags,
paths, nor URLs (subcommands, remote names, branch names, refs) SHALL
remain in the verb chain — the extractor SHALL NOT attempt to distinguish
subcommands from positional operands. For shell approval units, `&&`,
`||`, and `;` SHALL split into separate units, while `|` SHALL remain
inside the current unit. For `bash -c` or `sh -c` wrappers, the inner
command SHALL be extracted and scanned recursively.

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
- **THEN** the pattern is `git push origin main`
- **AND** the bare-word operands `origin` and `main` remain in the verb
  chain because greedy extraction does not strip positional operands

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

### Requirement: IToolApprovalMatcher extension point

The system SHALL define an `IToolApprovalMatcher` interface for tool-specific
pattern extraction and matching. Shell SHALL implement verb-chain matching. A
default implementation SHALL provide tool-name-level matching for tools without
a custom matcher.

#### Scenario: Shell uses verb-chain matcher

- **GIVEN** a `shell_execute` tool call with command `npm install lodash`
- **WHEN** the approval system extracts the pattern
- **THEN** `ShellApprovalMatcher` extracts `npm install`

#### Scenario: Approved pattern matches invocation

- **GIVEN** `git push` is in the Personal approval list for `shell_execute`
- **WHEN** the agent runs `git push --tags origin main`
- **THEN** `ShellApprovalMatcher.IsApproved` returns true (prefix match)

### Requirement: Mid-turn approval pause

The system SHALL pause individual tool execution tasks when approval is required
without blocking other tool calls in the same batch. The pause SHALL use a
`TaskCompletionSource` that completes when the session actor receives an approval
response. The pause SHALL wait indefinitely for user response — the system
SHALL NOT auto-deny on a timer. Operators take as long as they need to
evaluate a prompt; a clock-driven auto-deny silently transitions the
workflow to a denied state and manufactures race conditions (late clicks
landing in already-terminated workflows) for zero security benefit.

#### Scenario: Approval-pending tool blocks while others complete

- **GIVEN** a batch of 3 tool calls: `web_search`, `shell_execute`, `file_read`
- **AND** `shell_execute` requires approval
- **WHEN** the batch executes
- **THEN** `web_search` and `file_read` execute in parallel immediately
- **AND** `shell_execute` blocks waiting for approval
- **AND** the session actor remains responsive to messages

#### Scenario: Approval pause waits indefinitely for user response

- **GIVEN** an approval prompt has been emitted
- **AND** the user has not yet clicked any button
- **WHEN** an arbitrarily long time passes (minutes, hours, until daemon restart)
- **THEN** the workflow remains paused on the TaskCompletionSource
- **AND** no clock-driven transition to `TimedOut` occurs
- **AND** when the user eventually clicks, the workflow resumes from that state

#### Scenario: Approved tool executes and returns result

- **GIVEN** a tool is blocked waiting for approval
- **WHEN** the user approves (once or always)
- **THEN** the tool executes and returns its result
- **AND** the approval is cached (session-only or persistent depending on choice)

#### Scenario: Denied tool returns denial message

- **GIVEN** a tool is blocked waiting for approval
- **WHEN** the user denies
- **THEN** the tool returns "Command denied by user" as the tool result
- **AND** no command is executed

### Requirement: ToolInteractionRequest/Response protocol

The system SHALL define a `ToolInteractionRequest` session output and
`ToolInteractionResponse` session command for channel-mediated approval
interactions.
The interaction `Kind` SHALL identify the interaction type (`approval` for v1).
`ToolInteractionRequest` SHALL be a lifecycle output (always delivered regardless
of `OutputFilter`).

`ToolInteractionRequest` SHALL include a `DirectoryRoots` field containing
reusable directory roots extracted from the tool invocation. When non-empty and
the user selects `Approve for this chat` or `Approve always`, the session actor
SHALL record the directory roots instead of exact shell approval patterns.

#### Scenario: Approval request emitted as session output

- **GIVEN** a tool requires approval
- **WHEN** the pipeline detects the approval requirement
- **THEN** a `ToolInteractionRequest` with `Kind=approval` is emitted
- **AND** it includes `CallId`, `ToolName`, the command/pattern, and available
  options (approve once, approve for this chat, approve always, deny)

#### Scenario: Approval request includes directory roots

- **GIVEN** a shell command targets a file under `/home/.netclaw/logs/`
- **WHEN** the approval request is generated
- **THEN** `ToolInteractionRequest.DirectoryRoots` contains `/home/.netclaw/logs/`
- **AND** the request still includes the exact blocked approval pattern for retry

#### Scenario: Channel routes response back to session

- **GIVEN** a `ToolInteractionRequest` has been emitted
- **WHEN** the user selects an option (for MVP Slack, via text reply)
- **THEN** the channel sends a `ToolInteractionResponse` to the session actor
- **AND** the response includes `CallId` and the selected option key

### Requirement: Approval provenance stays inclusive while third-party adopted policy is separate

Approval prompts and stored approval context SHALL preserve truthful adopted
provenance for any non-empty adopted window.

For approval context:

- `HasAdoptedContext` SHALL mean the adopted window is non-empty.
- Adopted-speaker provenance SHALL list all adopted sender ids present in that
  window, including self-only adopted history.
- `HasThirdPartyAdoptedContext` MAY be carried as a separate policy field, but it
  SHALL be derived independently and SHALL NOT replace or trim the full adopted
  provenance.

This clarification SHALL NOT alter the trust model: approval requests still
originate only from the current authorized executable message, and adopted
context remains quoted, non-executable background.

#### Scenario: Self-only adopted history still appears in approval provenance

- **GIVEN** the current authorized message requires tool approval
- **AND** the adopted window is non-empty
- **AND** every adopted sender id matches the current authorized sender
- **WHEN** the approval prompt and stored context are created
- **THEN** `HasAdoptedContext` is true
- **AND** adopted-speaker provenance includes that sender id
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Third-party adopted history preserves full provenance

- **GIVEN** the current authorized message requires tool approval
- **AND** the adopted window includes sender ids `U111` and `U222`
- **WHEN** the approval prompt and stored context are created
- **THEN** adopted-speaker provenance includes both `U111` and `U222`
- **AND** `HasThirdPartyAdoptedContext` is true

#### Scenario: Empty adopted window omits adopted provenance entirely

- **GIVEN** the current authorized message requires tool approval
- **AND** the turn has no adopted window
- **WHEN** the approval prompt and stored context are created
- **THEN** `HasAdoptedContext` is false
- **AND** no adopted-speaker provenance is included

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

### Requirement: Global grant precedence over folder-scoped grants

A persisted global `ApprovalEntry` (`directory: null`) SHALL authorize its
verb in every directory. When both a global entry and one or more
folder-scoped entries exist for the same verb within the same audience and
tool, the global entry SHALL be sufficient for approval in any directory;
the folder-scoped entries become redundant for matching but SHALL be
retained on disk. Adding a global grant SHALL NOT remove, supersede, or
rewrite existing folder-scoped grants for the same verb — retaining them
preserves the operator's ability to revoke the global grant and fall back
to the narrower folder-scoped grants.

The matcher SHALL evaluate a candidate against every persisted
`ApprovalEntry` for the verb and approve when any entry matches. It SHALL
NOT stop at the first verb-matching entry whose directory check fails.

#### Scenario: Global grant approves verb in an unrelated directory

- **GIVEN** `tool-approvals.json` contains both
  `{"verb":"dotnet","directory":"~/repos/foo/"}` and
  `{"verb":"dotnet","directory":null}`
- **WHEN** the agent invokes `dotnet --info` with cwd `~/repos/bar/`
- **THEN** the matcher returns approved via the global entry
- **AND** no prompt is rendered

#### Scenario: Adding a global grant retains folder-scoped grants

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"dotnet","directory":"~/repos/foo/"}`
- **WHEN** the user clicks `Always anywhere` for verb `dotnet`
- **THEN** `tool-approvals.json` contains both the existing folder-scoped
  entry and a new `{"verb":"dotnet","directory":null}` entry
- **AND** the folder-scoped entry is NOT removed or rewritten

#### Scenario: Revoking a global grant restores folder-scoped scope

- **GIVEN** `tool-approvals.json` contains both
  `{"verb":"dotnet","directory":"~/repos/foo/"}` and
  `{"verb":"dotnet","directory":null}`
- **WHEN** an operator removes the `{"verb":"dotnet","directory":null}`
  entry via `netclaw approvals revoke`
- **THEN** `dotnet` invocations with cwd under `~/repos/foo/` still
  auto-approve via the retained folder-scoped entry
- **AND** `dotnet` invocations outside `~/repos/foo/` prompt again

### Requirement: Channel approval capability

Channels SHALL declare whether they support interactive approval via a
capability flag. When a tool requires approval and the active channel does NOT
support it, the system SHALL immediately deny the tool with reason
`channel_does_not_support_approval`. The system SHALL NOT hang or timeout.

#### Scenario: Unsupported channel auto-denies

- **GIVEN** the headless channel (no interactive user)
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes `shell_execute`
- **THEN** the tool is immediately denied with
  `channel_does_not_support_approval`

#### Scenario: Supported channel renders approval prompt

- **GIVEN** the Slack channel (supports interactive approval)
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes an unapproved `shell_execute` command
- **THEN** the channel renders the approval prompt as a text A/B/C/D reply flow

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

### Requirement: Approval entry creation timestamp

`ApprovalEntry` SHALL carry an optional `createdAt` field — a
`DateTimeOffset` serialized as the ISO-8601 JSON property `createdAt` —
recording when the grant was first persisted. The field SHALL be
populated by `ToolApprovalStore.AddApproval` at write time using an
injected `TimeProvider` (`TimeProvider.System` in production), so the
daemon and the operator CLI stamp grants identically.

The `createdAt` field SHALL be additive and optional on disk. Reading a
`tool-approvals.json` file whose entries lack `createdAt` SHALL succeed
and yield entries with a `null` timestamp. The on-disk schema version
SHALL remain `2`; adding `createdAt` SHALL NOT bump the version and
SHALL NOT cause an existing file to be quarantined.

`createdAt` SHALL NOT participate in approval-entry equality. Two
entries with the same verb and directory but different (or absent)
timestamps SHALL still be considered the same grant by
`ToolApprovalEntryComparer`. `AddApproval` SHALL remain idempotent: when
an equivalent grant already exists, the existing entry — and therefore
its original `createdAt` — SHALL be left in place and SHALL NOT be
restamped.

#### Scenario: New grant is stamped with the current time

- **GIVEN** a `ToolApprovalStore` constructed with a `TimeProvider`
- **WHEN** `AddApproval` persists a new `(verb, directory)` entry
- **THEN** the stored entry's `createdAt` equals the provider's current
  time
- **AND** the serialized JSON includes a `createdAt` property

#### Scenario: Legacy entry without a timestamp reads back as null

- **GIVEN** a `version: 2` `tool-approvals.json` whose entries have no
  `createdAt` property
- **WHEN** the store loads the file
- **THEN** each entry's `createdAt` is `null`
- **AND** the file is NOT quarantined to `tool-approvals.json.v1.bak`
- **AND** the store's schema version remains `2`

#### Scenario: Idempotent re-grant preserves the original timestamp

- **GIVEN** a persisted entry `(git push, /home/user/repos/foo)` stamped
  at time T1
- **WHEN** `AddApproval` is called again for the same verb and directory
  at a later time T2
- **THEN** `AddApproval` reports no new entry was appended
- **AND** the stored entry's `createdAt` is still T1

#### Scenario: Timestamp does not affect matching or equality

- **GIVEN** a persisted entry `(git push, null)` stamped at any time
- **WHEN** the agent invokes `git push` and the matcher evaluates the
  entry
- **THEN** the match result is identical to the result for an entry with
  a `null` `createdAt`
- **AND** `ToolApprovalEntryComparer.Equals` treats the two entries as
  equal

### Requirement: Approval-gate near-miss diagnostics

The approval gate SHALL log a near-miss diagnostic when it marks a
candidate pattern unapproved AND at least one persisted `ApprovalEntry`
exists for the same audience and tool whose `verb` equals the
candidate's verb chain. The diagnostic SHALL explain why each same-verb
grant failed to match and SHALL identify the grant (verb, directory
scope, and `createdAt`) and the reason it did not match — for example
the candidate's effective directory is not under the grant's directory,
a symlink segment lies along the path between the grant directory and
the effective directory, or the verbs differ only by case.

The diagnostic SHALL be emitted to the daemon log only. It SHALL NOT
appear in the approval prompt body and SHALL NOT alter the gate's
decision — it is read-only instrumentation. When no persisted entry
shares the candidate's verb, no near-miss diagnostic SHALL be emitted
(a first-time prompt has nothing to diagnose).

#### Scenario: Directory-scoped near-miss is logged

- **GIVEN** a persisted entry `(git push, /home/user/repos/foo)`
- **WHEN** the agent invokes `git push` with cwd
  `/home/user/repos/bar` and the gate marks it unapproved
- **THEN** the daemon logs a near-miss diagnostic naming the grant, its
  `createdAt`, and the reason the cwd is not under the grant directory
- **AND** the approval prompt body is unchanged

#### Scenario: First-time prompt emits no near-miss diagnostic

- **GIVEN** no persisted entry exists whose verb equals `terraform apply`
- **WHEN** the agent invokes `terraform apply` and the gate marks it
  unapproved
- **THEN** no near-miss diagnostic is logged
- **AND** the approval prompt is emitted normally

#### Scenario: Diagnostic does not change the gate decision

- **GIVEN** a persisted entry whose verb matches the candidate but whose
  directory does not
- **WHEN** the gate evaluates the candidate
- **THEN** the candidate remains unapproved
- **AND** the user is still prompted

