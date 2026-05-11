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

The system SHALL extract verb-chain prefix patterns from shell commands using
tokenization. The verb chain SHALL consist of non-flag tokens from the start of
the command until the first flag (`-`), path, or URL argument. For shell
approval units, `&&`, `||`, and `;` SHALL split into separate units, while `|`
SHALL remain inside the current unit.
For `bash -c` or `sh -c` wrappers, the inner command SHALL be extracted and
scanned recursively.

When a shell approval unit has no reusable directory roots, the system SHALL use
exact approval behavior for that unit.

#### Scenario: Verb chain extracted from simple command

- **GIVEN** the command `git push origin main`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `git push`

#### Scenario: Verb chain stops at flag

- **GIVEN** the command `ls -la /tmp`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `ls /tmp`

#### Scenario: Multi-level verb chain

- **GIVEN** the command `docker compose up -d`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `docker compose up`

#### Scenario: Control operators create separate approval units

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** `git add`, `git commit`, and `git push` are checked as separate
  approval units against the approval state surfaced through
  `IToolApprovalService`

#### Scenario: Unapproved compound segments batched in one prompt

- **GIVEN** `git add` is approved but `git commit` and `git push` are not
- **WHEN** the command `git add . && git commit -m "fix" && git push` is checked
- **THEN** a single approval prompt lists both `git commit` and `git push`
- **AND** the full compound command is shown for context

#### Scenario: bash -c inner command scanned recursively

- **GIVEN** the command `bash -c "git push --force"`
- **WHEN** approval and hard deny are checked
- **THEN** the inner command `git push --force` is extracted and scanned
- **AND** pattern `git push` is checked through `IToolApprovalService`

#### Scenario: Pipeline stays in one approval unit for root matching

- **GIVEN** `/home/.netclaw/logs/` is in the approved `shell_execute` roots
- **WHEN** the agent runs `grep "error" /home/.netclaw/logs/crash.log | wc -l`
- **THEN** the pipeline is treated as one approval unit
- **AND** the unit is auto-approved because its recognized local filesystem path
  stays under the approved root

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
response. A configurable timeout (default: 5 minutes) SHALL auto-deny if no
response arrives.

#### Scenario: Approval-pending tool blocks while others complete

- **GIVEN** a batch of 3 tool calls: `web_search`, `shell_execute`, `file_read`
- **AND** `shell_execute` requires approval
- **WHEN** the batch executes
- **THEN** `web_search` and `file_read` execute in parallel immediately
- **AND** `shell_execute` blocks waiting for approval
- **AND** the session actor remains responsive to messages

#### Scenario: Approval timeout auto-denies

- **GIVEN** an approval prompt has been emitted
- **WHEN** no response arrives within the configured timeout
- **THEN** the tool task unblocks with `ApprovalDecision.TimedOut`
- **AND** the tool result says "Approval timed out after X seconds"

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

The system SHALL store persistent approvals ("Approve Always" decisions) in
`~/.netclaw/config/tool-approvals.json`, separate from `netclaw.json`. The file
SHALL NOT be monitored by `ConfigWatcherService`. The file SHALL contain
per-audience sections with per-tool approval lists. For the shipped MVP shell
flow, the lists SHALL contain exact approvals and directory roots as applicable.
Approval lookup and recording SHALL be mediated by `IToolApprovalService`.

The file SHALL also be operator-editable via the `netclaw approvals` CLI
(see the `netclaw-cli` capability). The daemon SHALL pick up out-of-band
edits — whether made by direct file editing or by the CLI — on the next
approval check, without requiring a restart.

#### Scenario: Approve always persists directory root to file

- **GIVEN** the user clicks "Approve Always" for a command targeting
  `/home/.netclaw/logs/crash.log`
- **WHEN** the approval is processed
- **THEN** `/home/.netclaw/logs/` is added to the Personal `shell_execute` list
  in `tool-approvals.json`
- **AND** the daemon does NOT restart

#### Scenario: Persistent approvals loaded at startup

- **GIVEN** `tool-approvals.json` contains
  `{"personal":{"shell_execute":["git push", "/home/.netclaw/logs/"]}}`
- **WHEN** the daemon starts
- **THEN** `git push` is pre-approved for Personal audience shell commands
- **AND** later shell approval units whose recognized local paths all stay under
  `/home/.netclaw/logs/` are pre-approved

#### Scenario: Approve once is retry-scoped only

- **GIVEN** the user clicks "Approve Once" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Approve for this chat stores directory root in session

- **GIVEN** the user clicks "Approve For This Chat" for a command targeting
  `/home/.netclaw/logs/daemon.log`
- **WHEN** the approval is processed
- **THEN** the directory root is approved for the current session only
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session will prompt again

#### Scenario: Operator-applied revocation visible without restart

- **GIVEN** the daemon is running with a persisted approval for `git push`
- **WHEN** an operator removes that entry via `netclaw approvals revoke`
- **AND** a new approval check evaluates `git push`
- **THEN** the daemon re-loads the file and observes the entry is gone
- **AND** the user is prompted for approval again
- **AND** the daemon was not restarted

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

For `shell_execute`, `Approve once` SHALL remain exact blocked-call retry only.
It SHALL NOT create a reusable session approval, persistent approval, or
directory-root approval.

For `shell_execute`, when the user selects `Approve for this chat` (B) or
`Approve always` (C) and the shell approval unit contains one or more
recognized local filesystem paths, the system SHALL store directory roots for
that approval unit instead of verb-specific or command-pattern-specific shell
approvals.

Directory approvals SHALL be root-based and verb-agnostic. A later shell
approval unit SHALL be auto-approved only when every recognized local
filesystem path in that unit resolves under already approved roots.

If a shell approval unit yields no reusable local directory roots, directory
approval SHALL NOT apply and the system SHALL fall back to exact approval
behavior for that unit.

The system SHALL enforce minimum directory depth, path normalization,
boundary-safe containment, path traversal checks, and `ToolPathPolicy` as the
safety backstop for directory-root approvals. `ToolPathPolicy` SHALL resolve
symlinks along every component of a candidate path so that a planted symlink
under an approved root cannot be used to reach a protected path that lies
outside that root.

#### Scenario: Approve once retries only the blocked call

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash.log` requires approval
- **WHEN** the user selects `Approve once`
- **THEN** only the current blocked call is retried
- **AND** no reusable approval is recorded
- **AND** a later `cat /home/.netclaw/logs/other.log` prompts again

#### Scenario: Approve for this chat stores a reusable directory root

- **GIVEN** a shell command `cat /home/.netclaw/logs/crash-foo.log` requires approval
- **WHEN** the user selects `Approve for this chat`
- **THEN** the session-scoped approval stores the directory root `/home/.netclaw/logs/`
- **AND** a later `grep "error" /home/.netclaw/logs/daemon.log` in the same session
  does not prompt

#### Scenario: Approve always stores a reusable directory root

- **GIVEN** a shell command `grep -l "timeout" /home/.netclaw/logs/daemon.log`
  requires approval
- **WHEN** the user selects `Approve always`
- **THEN** `/home/.netclaw/logs/` is written to `tool-approvals.json` for
  `shell_execute`
- **AND** a future-session `ls /home/.netclaw/logs/archive.log` is auto-approved

#### Scenario: All recognized local paths in a unit must be covered

- **GIVEN** `/home/.netclaw/logs/` is approved for `shell_execute`
- **WHEN** the agent runs `cat /home/.netclaw/logs/app.log /home/.netclaw/config/netclaw.json`
- **THEN** the command still requires approval because not all recognized local
  filesystem paths fall under approved roots

#### Scenario: No reusable local roots falls back to exact approval behavior

- **GIVEN** a shell command `git push origin main` requires approval
- **WHEN** the user selects `Approve for this chat`
- **THEN** no directory root is stored
- **AND** the system falls back to exact approval behavior for `git push`

#### Scenario: Shallow directory root falls back to exact approval behavior

- **GIVEN** a shell command `cat /etc/passwd` requires approval
- **WHEN** directory-root extraction runs
- **THEN** the derived root `/etc/` is rejected as too shallow
- **AND** the system falls back to exact approval behavior

#### Scenario: Boundary-safe matching prevents prefix collisions

- **GIVEN** `/home/user/` is approved for `shell_execute`
- **WHEN** the agent runs `cat /home/usersecret/data.txt`
- **THEN** the command requires approval
- **AND** `PathUtility.IsWithinRoot` prevents the false positive

#### Scenario: Symlink under approved root cannot reach a protected path

- **GIVEN** `/home/user/safe/` is approved for `shell_execute`
- **AND** `/home/user/safe/leak` is a directory symlink whose target resolves
  to `/etc`
- **WHEN** the agent runs `cat /home/user/safe/leak/passwd`
- **THEN** the approval gate auto-approves the unit because the literal path
  is within the approved root
- **AND** `ToolPathPolicy.CommandReferencesDeniedPath` blocks execution because
  the canonical path resolves to `/etc/passwd` after symlink resolution along
  every path component

### Requirement: Directory root extraction via IToolApprovalMatcher

`IToolApprovalMatcher` SHALL define an `ExtractDirectoryRoots()` method that
returns reusable directory roots for a tool invocation.

For `shell_execute`, extraction SHALL operate on shell approval units. Units
SHALL split on `&&`, `||`, and `;`. Pipelines joined by `|` SHALL stay inside
the same approval unit.

`ShellApprovalMatcher` SHALL scan each approval unit for recognized local
filesystem paths, expand and normalize them, derive reusable parent directory
roots, and enforce minimum depth and path-safety checks. For `bash -c` or
`sh -c` wrappers, the inner command SHALL be extracted and scanned recursively.

`DefaultApprovalMatcher` and `FilePathApprovalMatcher` SHALL return empty lists.

#### Scenario: grep extracts a root from a later argument

- **GIVEN** the command `grep -l "timeout" /home/.netclaw/logs/daemon.log`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the root `/home/.netclaw/logs/` is extracted
- **AND** the search term `"timeout"` is ignored

#### Scenario: Pipeline stays in one approval unit

- **GIVEN** the command `grep "error" /home/.netclaw/logs/app.log | wc -l`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the pipeline is treated as one approval unit
- **AND** the root `/home/.netclaw/logs/` is extracted for that unit

#### Scenario: Control operators split approval units

- **GIVEN** the command `cat /home/.netclaw/logs/app.log && cat /home/.netclaw/config/netclaw.json`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the `&&` creates two approval units
- **AND** each unit is evaluated independently for reusable roots

#### Scenario: Glob paths use parent directory root

- **GIVEN** the command `ls /home/.netclaw/logs/crash-*.log`
- **WHEN** `ExtractDirectoryRoots` runs
- **THEN** the root `/home/.netclaw/logs/` is extracted
- **AND** the glob component does not become part of the stored root

### Requirement: Dynamic approval option labels

When directory roots are available, the system SHALL customize the approval
option labels to show the reusable root scope. The labels SHALL follow the
format:
- B: `"Approve in {directory-root} for this chat"`
- C: `"Approve in {directory-root} always"`

Options A ("Approve once") and D ("Deny") SHALL retain their default labels.

#### Scenario: Labels show reusable root scope for shell commands

- **GIVEN** a shell command `grep "error" /home/.netclaw/logs/app.log`
  requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads `Approve in /home/.netclaw/logs/ for this chat`
- **AND** option C reads `Approve in /home/.netclaw/logs/ always`

#### Scenario: Labels use defaults when no reusable directory root exists

- **GIVEN** a shell command `git push origin main` requires approval
- **WHEN** the approval prompt is generated
- **THEN** option B reads the default "Approve for this chat"
- **AND** option C reads the default "Approve always"
