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
overrides in `ToolOverrides`. The default `DefaultMode` SHALL be `Auto` for
tools without a stricter invocation-specific rule.

The init-generated Personal config SHALL explicitly write
`ApprovalPolicy.ToolOverrides.shell_execute = Approval` as the normal
shell-safe configuration. For a Personal shell invocation, an exact
`shell_execute` override SHALL select `Auto`, `Approval`, or `Deny`. The runtime
SHALL select `Approval` when that exact override is absent. This rule SHALL
apply when `ApprovalPolicy` is absent. It SHALL also apply when `DefaultMode`
is `Auto`. This fallback SHALL prevent a missing field from enabling host shell
without approval.

#### Scenario: Shell requires approval in init-generated Personal config

- **GIVEN** a Personal audience session whose generated config explicitly sets
  `ApprovalPolicy.ToolOverrides.shell_execute` to `Approval`
- **WHEN** the agent invokes `shell_execute`
- **THEN** `ToolAccessPolicy` marks the call as approval-gated
- **AND** `DispatchingToolExecutor` consults `IToolApprovalService` before execution
- **AND** if the command pattern is not approved, an approval prompt is emitted

#### Scenario: Missing Personal approval policy fails closed for shell

- **GIVEN** a Personal audience session with `ShellMode` set to `HostAllowed`
- **AND** the Personal profile has no `ApprovalPolicy`
- **WHEN** the agent invokes `shell_execute`
- **THEN** the runtime resolves the invocation to `Approval`
- **AND** the missing policy does not enable automatic shell execution

#### Scenario: Personal policy without an exact shell override fails closed

- **GIVEN** a Personal approval policy whose `DefaultMode` is `Auto`
- **AND** `ToolOverrides` has no exact `shell_execute` entry
- **WHEN** the agent invokes `shell_execute`
- **THEN** the runtime resolves the invocation to `Approval`

#### Scenario: Explicit Personal shell Auto override executes without approval

- **GIVEN** a Personal approval policy with an exact `shell_execute = Auto` override
- **WHEN** the agent invokes a command that passes earlier security gates
- **THEN** the tool executes without an approval prompt

#### Scenario: Tool in Auto mode executes without approval

- **GIVEN** a tool whose effective approval mode is `Auto` for the session's audience
- **WHEN** the agent invokes the tool
- **THEN** the tool executes immediately without an approval prompt

#### Scenario: Tool in Deny mode is always blocked

- **GIVEN** a tool whose effective approval mode is `Deny` for the session's audience
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

The system SHALL derive one candidate from every complete canonical
`ShellSyntaxTree.CommandOccurrence`. Candidate identity SHALL use the static
authored verb tokens reported by ShellSyntaxTree. Netclaw SHALL NOT parse an
executable's private subcommands, flags, options, or operands.

Every candidate SHALL retain its occurrence, redirects, effective and authored
value facts, real scope, and optional intent scope. Pipelines, lists, and loops
SHALL NOT hide later occurrences. Incomplete identity or unknown policy-relevant
facts SHALL remain strict.

Stored token-prefix phrases SHALL compare whole tokens with the selected
shell's case rule. Raw string prefix SHALL NOT authorize. Same-language wrapper
occurrences reported by ShellSyntaxTree SHALL remain visible. Cross-language
payloads SHALL remain arguments to the native host command.

Display and persistence SHALL keep existing spoof protections. Raw source SHALL
remain verbatim in the prompt only. CR, LF, bidi controls, malformed quoting,
and multiword free-text SHALL NOT enter a stored phrase. Path evidence SHALL
remain available to directory policy. Candidate normalization SHALL be the same
for actor match, prompt options, and persistence.

#### Scenario: Token-prefix grant covers a greedy candidate

- **GIVEN** a Bash token-prefix grant `git push`
- **AND** ShellSyntaxTree reports tokens `git`, `push`, `upstream`
- **WHEN** actor matching compares them
- **THEN** the grant covers the candidate
- **AND** no Git-specific remote rule runs

#### Scenario: Prefix collision does not match

- **GIVEN** a grant with tokens `git`, `push`
- **WHEN** the candidate tokens are `git`, `push-force`
- **THEN** the grant does not match

#### Scenario: All occurrences remain visible

- **WHEN** source is `inspect && head file; wc file`
- **THEN** candidates exist for `inspect`, `head`, and `wc`
- **AND** coverage for one cannot hide another

#### Scenario: Same-language wrapper exposes inner occurrences

- **WHEN** ShellSyntaxTree reports a static `bash -c` inner occurrence
- **THEN** that occurrence receives its own candidate and deny evaluation
- **AND** Netclaw does not decode the wrapper itself

#### Scenario: Cross-language payload stays external data

- **GIVEN** the canonical shell is Bash
- **WHEN** Bash invokes `pwsh -Command 'Get-Content ./a.txt'`
- **THEN** `pwsh` is the Bash external-command candidate
- **AND** the inline payload is not parsed as native PowerShell

#### Scenario: Multi-line or bidi content cannot persist

- **WHEN** a candidate contains multi-line, carriage-return, or bidi-controlled
  authored content
- **THEN** that content is excluded from the normalized grant phrase
- **AND** the prompt retains a separately escaped verbatim display
- **AND** no reusable option is offered if a clean phrase cannot be formed

#### Scenario: Dynamic identity stays one-time

- **WHEN** source is `"$1" --version`
- **THEN** no stored phrase or safe policy covers the identity
- **AND** only one-time approval and deny are offered

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

Tool-batch start, per-tool results, approval requests, approval resolutions,
and abandonment closures SHALL be journaled so the pause survives idle
passivation, turn failure, and actor restart without relying on snapshots to
carry unjournaled in-flight state. On recovery the session SHALL restore pending
interactions from the journal and, when an approval response arrives, SHALL
re-drive only unresolved tool calls that are eligible to run. An approval
response whose call is not pending and cannot be reconstructed from session
history SHALL fail loud with a user-visible "approval prompt expired" message;
it SHALL NOT be silently discarded.

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

#### Scenario: Pending approval persisted to the session journal

- **GIVEN** a tool call has emitted an approval prompt and the turn is paused
- **WHEN** the session persists the approval request
- **THEN** the journal SHALL include the pending tool interaction, keyed by call id
- **AND** the persisted interaction SHALL carry the requester identity, audience,
  and trust context needed to re-drive the call faithfully

#### Scenario: Pending approval survives idle passivation and cold recovery

- **GIVEN** a session with a pending approval prompt is idle-passivated and stopped
- **WHEN** the session is cold-respawned and recovers from its journal/snapshot path
- **THEN** the recovered session SHALL restore the pending tool interaction
- **AND** an approval response arriving afterward SHALL re-drive the tool batch
  and continue the turn
- **AND** the same requester-only `CanApprove` check and grant-persistence rules
  apply as on the live path

#### Scenario: Re-drive does not repeat completed sibling calls

- **GIVEN** a tool batch was interrupted after one sibling completed and journaled its result
- **AND** another sibling was still pending approval when the session stopped
- **WHEN** the recovered session re-drives unresolved calls from the last durable assistant tool-call message
- **THEN** the already-completed sibling call SHALL NOT execute again
- **AND** its journaled tool result SHALL remain in the transcript used for the follow-up LLM call

#### Scenario: Approval response for an expired call fails loud

- **GIVEN** a session has no pending interaction for the responded call id
- **AND** the call cannot be reconstructed from session history
- **WHEN** an approval response arrives for that call id
- **THEN** the session SHALL emit a user-visible message that the approval
  prompt has expired and the request should be re-issued
- **AND** the session SHALL NOT silently drop the response

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
`~/.netclaw/config/tool-approvals.json` using version 3. New shell entries SHALL
contain canonical shell, match kind, immutable verb-token array, optional
absolute directory, and creation timestamp. Null directory SHALL mean global.

On first successful version-2 load, the daemon SHALL back up the original file.
Every valid version-2 shell phrase SHALL migrate as an exact-only legacy
phrase. No migrated phrase SHALL gain token-prefix authority. A valid v2 entry whose
phrase contains controls or cannot be represented safely SHALL be omitted with
a bounded migration diagnostic and SHALL NOT authorize. A structurally invalid
v2 file SHALL fail as a whole. The version-3 write SHALL be atomic.

The daemon SHALL observe valid operator edits on the next approval check. CLI
list, add, and revoke SHALL understand both token-prefix and legacy-exact
entries. It SHALL NOT silently downgrade a version-3 file.

An absent file SHALL be a valid empty store. An absent-version or version-1
file SHALL follow the existing quarantine path and become an empty version-3
store only after a successful atomic write. Malformed JSON, partial version-3
corruption, invalid enum or token values, and unsupported future versions SHALL
make the store unavailable: no entry SHALL authorize and an
approval-dependent call SHALL terminate deny with `ApprovalStoreUnavailable`.
A future-version file SHALL remain untouched.

Failure to create the v2 backup SHALL abort migration and leave v2 untouched.
Failure of atomic replacement SHALL retain v2 and any completed backup, make
the store unavailable for that check, and permit a later load to retry. The
loader SHALL NOT salvage individual grants from a partially corrupt version-3
or structurally invalid version-2 file.

#### Scenario: New global entry stores tokens

- **WHEN** the user approves `git push` everywhere under native Bash
- **THEN** version 3 stores shell `Bash`, match `TokenPrefix`, tokens
  `["git", "push"]`, and null directory

#### Scenario: Ambiguous v2 phrase remains exact

- **GIVEN** a v2 verb contains quoting or an escape
- **WHEN** migration runs
- **THEN** the entry becomes `LegacyExact`
- **AND** it does not gain token-prefix authority

#### Scenario: Invalid migrated entry cannot authorize

- **GIVEN** a v2 entry contains controls or cannot be represented safely
- **WHEN** migration runs
- **THEN** the entry is omitted with a bounded migration diagnostic
- **AND** no candidate matches it

#### Scenario: Revocation is visible without restart

- **WHEN** an operator revokes a version-3 entry through the CLI
- **THEN** the next actor snapshot excludes it
- **AND** a later call prompts if no other coverage exists

#### Scenario: Future schema fails closed without modification

- **GIVEN** `tool-approvals.json` declares a version newer than 3
- **WHEN** an approval-dependent shell call is checked
- **THEN** no persisted entry authorizes
- **AND** the call is denied with `ApprovalStoreUnavailable`
- **AND** the file is not rewritten or quarantined

#### Scenario: Backup failure preserves version 2

- **GIVEN** a valid version-2 store
- **AND** creation of `.v2.bak` fails
- **WHEN** migration is attempted
- **THEN** the version-2 source remains byte-identical
- **AND** no version-3 replacement is attempted
- **AND** the approval-dependent call fails closed

### Requirement: Global grant precedence over folder-scoped grants

A persisted global version-3 phrase (`directory: null`) SHALL authorize every
candidate matched by the phrase in its declared audience, tool, and canonical
shell. When both a global entry and folder-scoped entries exist for the same
typed phrase identity, the global entry SHALL be sufficient regardless of real
cwd. Folder-scoped entries SHALL remain on disk so revoking the global entry
restores the narrower authority.

The matcher SHALL evaluate every persisted entry whose canonical shell, match
kind, and phrase identity can cover the candidate. It SHALL NOT stop at the
first phrase match whose directory check fails. Adding a global entry SHALL
NOT remove, supersede, or rewrite a folder entry.

#### Scenario: Global token phrase wins outside folder scope

- **GIVEN** version 3 contains folder and global `TokenPrefix` entries with
  Bash tokens `["dotnet"]`
- **WHEN** Bash invokes `dotnet --info` outside the folder
- **THEN** the global entry covers the candidate
- **AND** no prompt is rendered

#### Scenario: Adding global phrase retains narrower phrase

- **GIVEN** version 3 contains a folder-scoped Bash phrase `["dotnet"]`
- **WHEN** the user approves the same phrase everywhere
- **THEN** both entries remain on disk with their original timestamps
- **AND** revoking the global entry restores folder-only matching

### Requirement: Channel approval capability

Channels SHALL declare whether they support interactive approval via a
capability flag. When a tool requires approval and the active channel does NOT
support it, the system SHALL immediately deny the tool with reason
`channel_does_not_support_approval`. The system SHALL NOT hang or timeout.

Channels that support interactive approval SHALL render approval prompts using
their richest available interaction surface and SHALL always provide a
deterministic text fallback path with equivalent decision options when the
rich interaction surface is unavailable or not configured.

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

#### Scenario: Mattermost channel renders interactive approval buttons

- **GIVEN** the Mattermost channel (supports interactive approval)
- **AND** interactive approvals are configured for the Mattermost channel
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes an unapproved `shell_execute` command
- **THEN** the channel renders the approval prompt as Mattermost interactive
  buttons
- **AND** a clicked button is routed as a `ToolInteractionResponse`

#### Scenario: Mattermost channel falls back to deterministic text options

- **GIVEN** the Mattermost channel (supports interactive approval)
- **AND** interactive approvals are not configured for the Mattermost channel
- **WHEN** the agent invokes an unapproved `shell_execute` command
- **THEN** the channel renders a deterministic A/B/C/D text approval prompt
- **AND** text replies map to equivalent approval decisions

### Requirement: Directory-root approvals for shell_execute

Global token phrases SHALL not require an exact cwd. Folder phrases SHALL
require the candidate's real exact scope under the stored directory, with
normalization, boundary-safe containment, minimum-depth, traversal, and symlink
checks. Intent scope SHALL never satisfy a folder grant.

`Once` SHALL retry only the blocked request. `This chat` SHALL create typed
session entries. `Always here` SHALL persist one clean version-3 entry per
persistable candidate at the real prompt scope. `Always anywhere` SHALL persist
global entries. Unknown or synthetic-only scope SHALL omit `Always here`.

#### Scenario: Global grant works with unknown cwd

- **GIVEN** a global token phrase covers a static candidate
- **WHEN** its joined cwd is unknown and path facts are otherwise strict-safe
- **THEN** the actor may cover the candidate globally

#### Scenario: Folder grant rejects synthetic-only scope

- **GIVEN** a folder grant under `/work/project`
- **AND** only intent scope is `/work/project`
- **WHEN** the real candidate scope is unknown
- **THEN** the folder grant remains a near miss

#### Scenario: One persistent click stores each clean candidate

- **GIVEN** three candidates are clean and persistable
- **WHEN** the user selects a persistent option
- **THEN** one typed entry is stored for each candidate
- **AND** no uncovered candidate is silently omitted

#### Scenario: Symlink cannot widen folder authority

- **GIVEN** a path under a folder grant crosses a symlink to protected space
- **WHEN** policy evaluates it
- **THEN** folder coverage fails
- **AND** protected-path policy denies when applicable

### Requirement: Reviewed diagnostic auto-allow in declared safe spaces

The system SHALL load an embedded immutable per-platform policy catalog.
`ReviewedDiagnostic` SHALL classify only the shell-authored invocation.
Runtime user overrides SHALL NOT widen the catalog.

No accepted authored argument shape SHALL select a child executable, select a
caller-authored output file, request destructive persistent state, or request
a remote mutation. Tool-private metadata or cache refresh SHALL remain outside
this claim. Ambient executable configuration and executable-discovered paths
SHALL also remain outside this claim.

Redirects, parser-owned filesystem values, provider paths, and unknown shell
expansions SHALL remain separate strict effects. Bounded shell-local output
variables MAY remain eligible. Any unresolved later use SHALL remain strict.

Safe policy SHALL refine only uncovered candidates. It SHALL require reviewed
phrase coverage, an allowed real or eligible intent scope, no symlink segment,
no writing redirect, and no unknown explicit path fact. Hard deny and protected
paths SHALL run first. Personal and Team safe roots SHALL be session directory
plus declared project directory. Public SHALL use session directory only.

`find`, `awk`, `rg`, and `sort` SHALL not be reviewed-safe. Production policy
code SHALL contain no executable-specific flag exceptions. PowerShell provider
paths SHALL retain existing strict checks.

Reviewed-safe phrase identity SHALL use canonical ShellSyntaxTree token
prefixes. Legacy display and compatibility strings SHALL NOT establish
reviewed-safe coverage.

An authored argument before the matched phrase completes SHALL prevent
reviewed-safe coverage. The check SHALL use parser-owned element order.

A known `AuthoredPathShape` SHALL be conservative negative evidence only.
Every represented authored value SHALL resolve beneath an eligible safe root.
Unknown or unsupported domains SHALL prevent reviewed-safe coverage. A lexical
path shape SHALL NOT create filesystem authority.

An `Exact` or `FiniteSet` ShellSyntaxTree 0.3.5
`AuthoredNonFileSystemValue` SHALL suppress weaker path interpretations for
that argument only. It SHALL NOT grant authority. Other arguments, redirects,
effects, and `AuthoredFileSystemValue` facts SHALL remain independent. Unknown,
unsupported, or contradictory domains SHALL keep reviewed-safe policy strict.

#### Scenario: Reviewed diagnostic in project scope is covered

- **GIVEN** `head` is reviewed safe
- **AND** its real scope is under a Personal project root
- **WHEN** every earlier stage passes
- **THEN** safe policy covers that candidate

#### Scenario: Global argument before a reviewed phrase stays strict

- **GIVEN** `git status` is a reviewed diagnostic phrase
- **WHEN** the authored command is `git -c include.path=/tmp/config status`
- **THEN** reviewed-safe policy does not cover the candidate
- **AND** Netclaw does not parse Git's private option grammar

#### Scenario: Hidden option path outside the safe root stays strict

- **GIVEN** `grep` is a reviewed diagnostic phrase
- **AND** ShellSyntaxTree marks `/tmp/patterns` with a POSIX path shape
- **WHEN** the authored command is `grep -f /tmp/patterns ./safe.txt`
- **THEN** reviewed-safe policy does not cover the candidate
- **AND** lexical path shape creates no new authority

#### Scenario: Path-shaped data beneath the safe root can remain eligible

- **GIVEN** a reviewed diagnostic receives `example/project` as data
- **AND** its possible local-path interpretation stays beneath the safe root
- **WHEN** all stronger shell facts pass
- **THEN** lexical path shape alone does not reject the candidate

#### Scenario: Audited tr data does not create a false path scope

- **GIVEN** `tr` is a reviewed diagnostic
- **AND** ShellSyntaxTree reports `Exact("\\n")` as authored non-filesystem data
- **WHEN** Bash evaluates `tr -d '\n'`
- **THEN** the lexical Windows path shape does not create a `/n` scope
- **AND** reviewed-safe policy may cover `tr` after every other guard passes

#### Scenario: Unknown command keeps path-shaped data strict

- **GIVEN** an unknown command receives `\n`
- **WHEN** no positive authored non-filesystem fact exists
- **THEN** the lexical path interpretation remains strict

#### Scenario: Unproved glob semantics stay strict

- **GIVEN** Bash evaluates `tr *.txt x`
- **AND** no positive authored non-filesystem fact exists for the glob
- **THEN** reviewed-safe policy does not cover `tr`

#### Scenario: Independent redirect remains strict

- **GIVEN** Bash evaluates `tr -d '\n' > /external/out`
- **WHEN** the data argument has a positive non-filesystem fact
- **THEN** the output redirect still prevents reviewed-safe coverage

### Requirement: Guidance distinguishes file operations from shell semantics

Team and Personal guidance SHALL prefer first-party file tools for known file
reads, directory listings, and edits. It SHALL avoid shell for those operations
unless shell behavior is requested. Shell guidance SHALL retain
`shell_execute` for local repository search, builds, tests, VCS, and process
semantics. External discovery SHALL use built-in `web_search`. Page retrieval
SHALL use built-in `web_fetch`, not a shell HTTP client.

#### Scenario: Known file content avoids shell approval

- **GIVEN** the agent knows the target file or directory
- **WHEN** it needs content, a listing, or an edit
- **THEN** guidance prefers the matching first-party file tool
- **AND** it does not teach the agent to compose `cat`, `sed`, or `ls` chains
- **AND** deliberate shell-behavior requests remain shell work

#### Scenario: Shell workflows retain their execution tool

- **GIVEN** the task requires local repository search, build, test, VCS, or process semantics
- **WHEN** the agent selects a tool
- **THEN** guidance retains `shell_execute` for that work
- **AND** no approval-policy exception is added

#### Scenario: External search avoids the local shell

- **GIVEN** the task requires information from external sources
- **WHEN** the agent selects a search tool
- **THEN** guidance prefers built-in `web_search`
- **AND** page retrieval uses built-in `web_fetch`
- **AND** shell HTTP clients are not used for either operation
- **AND** it does not classify web search as local shell work

#### Scenario: Exact path scope does not declare a safe root

- **GIVEN** an agent has no declared project root for a user-named project
- **WHEN** a shell candidate contains an absolute path beneath that project
- **THEN** the path can provide the candidate's exact policy scope
- **AND** it does not add that project as a safe-space root
- **AND** model guidance tells the agent to call `set_working_directory` before
  the first shell or file tool call in that project
- **AND** guidance declares a named path before probing it with another tool
- **AND** guidance does not replace the first named path with its parent
- **AND** a rejected declaration precedes a user-provided fallback declaration
- **AND** the same rule applies when a subagent's exposed tools include
  `set_working_directory` and its inherited project differs
- **AND** the rule is absent when that tool is unavailable

#### Scenario: Undeclared project scope returns an agent correction

- **GIVEN** every shell candidate has a reviewed-safe phrase
- **AND** every effective directory is beneath the exact shell cwd
- **AND** the cwd is outside the declared session and project roots
- **AND** the cwd is not the platform temporary root
- **AND** `set_working_directory` is available to the agent
- **AND** the same filesystem policy used by `set_working_directory` accepts
  the exact cwd without substitution
- **WHEN** policy would otherwise request user approval
- **THEN** the system returns a scope-declaration correction to the agent
- **AND** it does not execute the command or request user approval
- **AND** the correction tells the agent to declare the exact cwd and retry the
  exact command unchanged

#### Scenario: Subagent scope correction precedes parent approval

- **GIVEN** a subagent submits eligible reviewed-safe shell work beneath an
  undeclared cwd
- **AND** its registered `set_working_directory` tool accepts that exact cwd
- **WHEN** policy would otherwise open the parent approval bridge
- **THEN** the subagent returns the same scope-declaration correction as a
  parent session
- **AND** it does not execute the shell command or request parent approval
- **AND** the authored tool call remains unchanged in model history

#### Scenario: Subagent declaration applies to the unchanged retry

- **GIVEN** a subagent received a scope-declaration correction
- **WHEN** it calls `set_working_directory` with the exact suggested cwd
- **THEN** the child replaces its local project-scope snapshot
- **AND** it reloads project instructions before the next model call
- **AND** an unchanged eligible shell retry uses the declared child scope
- **AND** the child does not replace the parent project directory

#### Scenario: Headless subagent declaration does not grant authority

- **GIVEN** a headless subagent received a scope-declaration correction
- **AND** it successfully declared the exact suggested cwd
- **WHEN** it retries the unchanged shell call
- **THEN** the declared child scope prevents another correction
- **AND** the retry follows ordinary headless authority rules
- **AND** the declaration does not grant reviewed-safe, session, or persistent
  authority

#### Scenario: Subagent keeps the approval bridge when scope cannot change

- **GIVEN** a subagent submits eligible reviewed-safe shell work beneath an
  undeclared cwd
- **AND** `set_working_directory` is absent or rejects that cwd
- **WHEN** policy requires approval
- **THEN** the scope-declaration correction does not apply
- **AND** the existing parent approval bridge handles the request

#### Scenario: Scope correction cannot hide unsafe work

- **GIVEN** any candidate lacks reviewed-safe phrase coverage
- **OR** any effective directory is outside the exact shell cwd
- **OR** the audience is Public
- **OR** the cwd is the platform temporary root
- **OR** `set_working_directory` is unavailable
- **OR** `set_working_directory` policy would reject or substitute the cwd
- **WHEN** policy evaluates the call
- **THEN** the scope-declaration correction does not apply in a parent session
  or subagent
- **AND** the normal approval or deny result remains

#### Scenario: Unsafe argument surface excludes whole phrase

- **GIVEN** any accepted argument can write or execute
- **WHEN** maintainers audit the catalog phrase
- **THEN** the phrase is excluded entirely
- **AND** no private flag branch compensates for it

#### Scenario: File redirect remains separate

- **WHEN** reviewed `head` writes through a shell redirect
- **THEN** safe policy does not cover the occurrence
- **AND** redirect path policy still applies

#### Scenario: Public project directory is not safe

- **GIVEN** a Public session has a project directory
- **WHEN** a reviewed diagnostic candidate runs only there
- **THEN** safe policy does not cover it

#### Scenario: PowerShell environment provider stays strict

- **WHEN** native PowerShell submits `Get-Content Env:SECRET`
- **THEN** the provider is not treated as filesystem safe space
- **AND** the call requires explicit authority or denial

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

The display text for a shell command SHALL be single-line. A command
containing embedded line breaks (LF or CR) SHALL be reconstructed from
its parse tree: statement separators render as explicit operators (`;`,
`&&`, `||`, `|`) and each multi-line argument or redirect target is
replaced with a `(N lines, M chars)` size summary instead of its
verbatim content (issue #1402) — channel renderers embed the display
text in single-line code fences, and dumping a multi-line quoted blob
verbatim corrupts the prompt layout. When the parser cannot decompose
the command, line breaks SHALL be flattened to spaces.

Commands containing heredocs or subshell groupings SHALL NOT be
display-reconstructed: the parser drops heredoc bodies from the tree
(only the `<<EOF` marker survives as a redirect target), so a
reconstruction would silently omit executable content the approver must
see — and subshell grouping does not survive the flat clause list, so a
reconstruction would misstate which statements a pipe or `&&` guard
applies to. Both fall back to the flattened raw command — ugly but
fully disclosed.

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

#### Scenario: Multi-line quoted argument summarized in display text

- **GIVEN** the agent invokes `shell_execute` with command
  `freshdesk ticket reply 605 --message "Hi,⏎We've rolled out a fix. Please verify."`
  where the quoted argument spans two lines
- **WHEN** the approval prompt is rendered
- **THEN** the display text reads
  `freshdesk ticket reply 605 --message (2 lines, 42 chars)`
- **AND** the display text contains no newline characters

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

Authorization SHALL use canonical ShellSyntaxTree completeness rather than a
second control-flow tokenizer. Supported static loops SHALL expose candidates.
Unsupported branches and runtime-generated loops SHALL remain strict.

An effective finite argument SHALL enter path policy when the parser-owned
`Argument.IsPath` role is true. ShellSyntaxTree 0.3.3 `Exact` and `FiniteSet`
`AuthoredFileSystemValue` facts SHALL also enter path policy. Unknown and all
other alternatives SHALL stay strict. `AuthoredPathShape` SHALL NOT substitute
for the stronger fact or create file authority.

A legacy scanner MAY add a denial when canonical analysis is incomplete. It
SHALL NOT allow, create candidates, create persistent options, or widen scope.

#### Scenario: ShellSyntaxTree 0.3.2 keeps D14 path coverage strict

- **GIVEN** ShellSyntaxTree 0.3.2 reports D14 finite authored values
- **AND** its effective argument has `Argument.IsPath` false
- **WHEN** the maintainer-approved authored-source policy evaluates it
- **THEN** the authored values do not create file authority
- **AND** lexical `AuthoredPathShape` does not cover the candidate

#### Scenario: ShellSyntaxTree 0.3.3 unlocks finite D14 path checks

- **GIVEN** ShellSyntaxTree 0.3.3 reports a finite D14
  `AuthoredFileSystemValue`
- **WHEN** the maintainer-approved authored-source policy evaluates it
- **THEN** each finite `cat` path passes `ToolPathPolicy`
- **AND** the presence of `for` alone does not force a prompt

#### Scenario: Runtime iterator stays one-time

- **WHEN** an iterator depends on command substitution output
- **THEN** the call offers only one-time approval and deny
- **AND** policy does not execute the substitution

#### Scenario: Deny-only scanner cannot authorize

- **GIVEN** canonical analysis is incomplete
- **WHEN** a legacy scan finds no deny pattern
- **THEN** the call remains unresolved
- **AND** it does not receive grant or safe coverage

### Requirement: Approval entry creation timestamp

Each version-3 approval entry SHALL carry optional ISO-8601 `createdAt` and
SHALL be stamped on first persistence using injected `TimeProvider`. Timestamp
SHALL NOT participate in equality. Phrase identity for idempotency SHALL be
canonical shell, match kind, token array or legacy-exact value, and directory.

Adding an equivalent entry SHALL preserve the existing entry and its original
timestamp. Version-2 migration SHALL preserve an existing timestamp exactly;
a missing timestamp SHALL remain null. Migration SHALL NOT restamp grants.

#### Scenario: New version-3 grant receives one timestamp

- **GIVEN** a deterministic `TimeProvider`
- **WHEN** a new typed phrase is persisted
- **THEN** `createdAt` equals the provider time
- **AND** re-adding the same phrase and directory does not change it

#### Scenario: Migration preserves timestamp absence

- **GIVEN** a valid version-2 entry without `createdAt`
- **WHEN** it migrates to a version-3 phrase
- **THEN** its `createdAt` remains null
- **AND** phrase equality remains independent of time

### Requirement: Approval-gate near-miss diagnostics

Near-miss diagnostics SHALL project only from the actor match trace. A near miss
SHALL identify candidate ID, grant kind, creation timestamp, and enum reason
such as token mismatch, shell mismatch, outside directory, or symlink. It SHALL
not include raw arguments, raw paths, or secrets and SHALL not rescan grants.

Diagnostics SHALL be operator-log-only and SHALL not alter the prompt or final
decision.

#### Scenario: Folder near miss uses actor evidence

- **GIVEN** a token phrase matches but folder scope does not
- **WHEN** the actor returns uncovered coverage
- **THEN** its trace contains `OutsideDirectory`
- **AND** logging uses that row without another store read

#### Scenario: First-time prompt has no fabricated near miss

- **GIVEN** no grant was considered for a candidate
- **WHEN** it remains uncovered
- **THEN** no grant near-miss row is emitted

### Requirement: Sub-agent approval bridge preserves prompt correlation

Sub-agent approval prompts SHALL use the same channel-agnostic `ToolInteractionRequest` contract as parent-session tool prompts. The request SHALL use a parent-scoped correlation call id that is unique per bridged approval request while preserving the child call id as part of the correlation value. The request SHALL preserve tool name, display text, exact blocked patterns, candidate verbs, per-candidate directories, cwd, messy-command flag, computed approval options, requester identity, principal, audience-derived authority, and adopted-context safety metadata from the parent turn authority context.

#### Scenario: Sub-agent prompt includes approval candidates and options
- **GIVEN** a sub-agent shell tool call requires approval
- **WHEN** the parent approval bridge emits the prompt
- **THEN** the prompt includes the exact blocked patterns shown to the user
- **AND** the prompt includes candidate verbs and per-candidate directories for grant persistence
- **AND** the prompt includes the same computed approval options the parent approval gate produced

#### Scenario: Sub-agent prompt carries adopted-context safety metadata
- **GIVEN** a sub-agent was spawned from a parent turn with adopted context
- **WHEN** the sub-agent emits an approval prompt
- **THEN** the prompt includes adopted-context and third-party adopted-context flags
- **AND** the prompt includes adopted speaker ids when present

#### Scenario: Duplicate child call ids do not share approval state
- **GIVEN** two bridged sub-agent approval waits have the same child-local tool call id
- **WHEN** the parent approval bridge emits prompts for both waits
- **THEN** each prompt uses a distinct parent-scoped call id
- **AND** approving one prompt cannot complete or authorize the other wait

### Requirement: Sub-agent approval responses do not execute expired work

Approval responses for sub-agent prompts SHALL execute a tool only while the originating sub-agent wait is still live and correlated to the pending call id. A response that arrives after the sub-agent wait was cancelled, completed, or abandoned SHALL fail closed as expired and SHALL NOT execute the gated tool.

#### Scenario: Late approval after cancellation is expired
- **GIVEN** a sub-agent approval prompt is pending
- **AND** the parent cancels the `spawn_agent` call before the user responds
- **WHEN** the user later approves the stale prompt
- **THEN** the sub-agent tool is not executed
- **AND** the response is treated as expired or no-longer-pending

#### Scenario: Live session response requires live approval wait
- **GIVEN** the parent session still has persisted prompt metadata for a sub-agent approval
- **AND** the child sub-agent approval wait has already been cancelled or completed
- **WHEN** an approval response arrives while the parent session is processing
- **THEN** the response is rejected as expired
- **AND** no approval grant is applied to execute stale sub-agent work

#### Scenario: Durable grant is written only after live wait is claimed
- **GIVEN** a sub-agent approval response requests a session or persistent grant
- **AND** the child approval wait is cancelled before the parent claims the response
- **WHEN** the response is handled
- **THEN** no durable approval grant is written
- **AND** the response is rejected as expired

#### Scenario: No bridge fails closed
- **GIVEN** a sub-agent tool call requires approval
- **AND** no parent approval bridge is available
- **WHEN** the tool executor reports that approval is required
- **THEN** no approval prompt is emitted
- **AND** the gated tool is not executed
- **AND** the sub-agent completes with a failed `SubAgentResult`

### Requirement: Approval pause persistence carries turn context

When a tool approval prompt is emitted from a session turn, the persisted approval request SHALL carry the original turn context as a single durable context record. The approval request MAY continue to carry tool-specific prompt data, option keys, candidates, and compatibility fields, but authority-bearing session context SHALL have one canonical persisted representation for new events.

#### Scenario: Approval request persists context record

- **GIVEN** a tool call requires approval during a session turn
- **WHEN** the session persists the approval request
- **THEN** the journaled event includes the turn context for the original request
- **AND** the pending interaction restored from that event carries the same context

#### Scenario: Tool-specific prompt data remains separate

- **GIVEN** an approval request includes command patterns, candidate verbs, option keys, and directory candidates
- **WHEN** the turn context is persisted with the approval request
- **THEN** tool-specific prompt data remains separate from the turn context
- **AND** the turn context does not become a dumping ground for approval-rendering state

### Requirement: Approval responses use persisted requester context

Approval response authorization SHALL use the requester and principal from the persisted turn context for the pending approval. A recovered approval response SHALL enforce the same requester-only approval rule as the live path, unless the original requester principal represents verified automation where channel-member approval is allowed.

#### Scenario: Non-requester approval rejected after recovery

- **GIVEN** a pending approval was restored with requester `U-requester`
- **WHEN** sender `U-other` approves the prompt
- **THEN** the approval response is rejected
- **AND** the tool is not redriven

#### Scenario: Verified automation approval remains approvable by channel member

- **GIVEN** a pending approval was restored with a verified automation principal
- **WHEN** a valid channel member approves the prompt
- **THEN** the approval response is accepted according to the same rule used on the live path
- **AND** the redrive uses the original turn context

### Requirement: Subagent approval evaluation uses the inherited parent cwd

The approval gate SHALL treat a subagent's `shell_execute` invocation as
having the cwd inherited from the parent session at spawn time, captured per
the `session-cwd` capability's "Resolved shell cwd flows to spawned subagents
as read-only snapshot" requirement. Persisted folder-scoped grants whose
directory contains the inherited cwd SHALL therefore auto-approve the
subagent invocation under the same rules as the parent session. Persisted
global grants (`directory: null`) SHALL continue to auto-approve regardless
of cwd, including when the inherited cwd is `null`. The matcher SHALL NOT
introduce a new short-circuit that bypasses persisted grants when the
inherited cwd is `null`; the existing
`ApprovalPatternMatching.MatchesShellApproval` semantics apply.

#### Scenario: Folder-scoped parent grant covers subagent invocation

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"dotnet build","directory":"/home/user/repos/foo/"}`
- **AND** the parent session's resolved cwd at subagent spawn is
  `/home/user/repos/foo/`
- **WHEN** the spawned subagent invokes `dotnet build` with no explicit
  `WorkingDirectory` argument
- **THEN** the matcher returns approved
- **AND** no approval prompt is rendered to the user

#### Scenario: Global grant covers subagent invocation with null cwd

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"netclaw stats","directory":null}`
- **AND** the spawned subagent has no inherited cwd (the parent had none
  either)
- **WHEN** the subagent invokes `netclaw stats`
- **THEN** the matcher returns approved regardless of the null cwd
- **AND** no approval prompt is rendered

#### Scenario: Folder-scoped parent grant does not match subagent with null cwd

- **GIVEN** `tool-approvals.json` contains
  `{"verb":"dotnet build","directory":"/home/user/repos/foo/"}`
- **AND** the spawned subagent has no inherited cwd
- **WHEN** the subagent invokes `dotnet build` with no explicit
  `WorkingDirectory` argument
- **THEN** the folder-scoped grant SHALL NOT match (no effective directory)
- **AND** the approval gate prompts the user with the header form
  `Approve dotnet build in (no working directory)?` as documented in this
  capability's "Five-button approval prompt with verb-and-directory framing"
  requirement
- **AND** the daemon log SHALL emit an `approval_near_miss` diagnostic with
  reason `NoCandidateDirectory` so the operator can see why the grant did
  not match

### Requirement: Subagent inherits parent session-scoped approvals

The approval actor SHALL walk a child scope toward its parent session using the
existing bounded `/subagent/` scope rule. Typed session phrases from the parent
SHALL cover matching child candidates. Unrelated sessions SHALL never share
coverage. The batch actor request SHALL perform this walk within the same
atomic snapshot as persistent matching.

#### Scenario: Parent session phrase covers child candidate

- **GIVEN** the parent chat has a typed session grant for `gh pr view`
- **WHEN** its child submits a matching candidate
- **THEN** the actor returns Session coverage
- **AND** no separate parent-grant scan runs

### Requirement: Approval evaluation uses admitted turn authority

Tool approval evaluation SHALL receive the same required admitted `TurnContext` as authorization and dispatch. Approval infrastructure SHALL NOT be nullable for tool-enabled sessions, and missing approval infrastructure SHALL NOT mean approval is bypassed.

#### Scenario: Approval policy cannot be supplied

- **GIVEN** a tool-enabled session cannot construct its required approval policy
- **WHEN** it attempts to execute a tool batch
- **THEN** execution fails before dispatch
- **AND** no tool runs as though approval were unnecessary

#### Scenario: Child approval retains parent turn authority

- **GIVEN** a child run forked from an admitted parent turn
- **WHEN** a child tool requires approval
- **THEN** approval evaluation uses the explicitly inherited turn authority
- **AND** no audience or source fallback is inferred

### Requirement: Shell policy uses the canonical grammar and dialect

The system SHALL select Bash only for native Bash execution and PowerShell only
for native Windows PowerShell execution. Bash invoking `pwsh` SHALL remain one
Bash external command. Every authorization stage SHALL share one canonical
ShellSyntaxTree analysis.

PowerShell SHALL use the selected dialect and `PwshInitialStateMode.Unknown`.
Netclaw SHALL use effective values for runtime and deny policy. It MAY use
authored values only for the approved approval perspective. It SHALL route
ShellSyntaxTree 0.3.3 authored filesystem values through path policy. Unknown
policy-relevant values SHALL not create reusable or safe coverage.

Netclaw SHALL consume ShellSyntaxTree 0.3.4 working-directory effects for the
bounded Bash causal projection. It SHALL NOT derive equivalent effects from
command names or executable-private grammar.

Deny-only defensive scans MAY deny incomplete input but SHALL never authorize
it.

#### Scenario: PowerShell pipeline evaluates every occurrence

- **WHEN** native Windows PowerShell submits a pipeline
- **THEN** every stage receives a candidate or strict finding
- **AND** one covered stage cannot hide an uncovered stage

#### Scenario: Bash does not cross-parse PowerShell payload

- **WHEN** native Bash submits `pwsh -Command 'Get-Content ./a.txt'`
- **THEN** policy evaluates the Bash `pwsh` occurrence
- **AND** it does not create a native PowerShell child candidate

#### Scenario: Authored facts do not replace effective deny facts

- **GIVEN** an argument has finite `AuthoredValue` but unknown effective value
- **WHEN** hard deny or runtime path policy evaluates it
- **THEN** those stages retain the effective uncertainty
- **AND** authored facts are limited to the approved matching perspective

### Requirement: Shell policy coordinator preserves actor ownership

The system SHALL evaluate a shell call through one coordinator with three
phases: synchronous preflight, one asynchronous approval-actor batch match, and
deterministic completion.

Preflight SHALL snapshot the existing `ToolExecutionContext` and exact
`ToolApprovalAttempt.OneTimeApprovedPatterns` set. Those legacy-named strings
SHALL remain `OneTimeApprovalKeys` binding filtered phrase and effective
directory. Preflight SHALL build one canonical
`ShellCommandAnalysis`, apply hard deny and protected paths, resolve approval
mode, build candidates, and preserve the existing noninteractive trust-zone
gate. If preflight is not terminal, `DispatchingToolExecutor` SHALL send
exactly one typed batch request to `ToolApprovalActor`.

`ToolApprovalActor` SHALL atomically snapshot inherited session and persistent
grants. It SHALL return one match result per stable candidate ID. It SHALL NOT
own or inspect one-time approval state. The coordinator SHALL import actor
coverage, apply safe policy to still-uncovered candidates, validate the
invocation-owned one-time set exactly, and SHALL NOT rescan grants.

Reviewed-safe phrase coverage SHALL cover a candidate only when the run has
interactive approval capability. A run without that capability SHALL require
explicit one-time, session, or persistent authority for every candidate that
is not an approval-exempt side effect.

The actor result SHALL include typed persistent-store status. An absent store
file SHALL be ready with an empty snapshot. Expected corruption or migration
failure SHALL be unavailable. Completion SHALL allow a call fully covered by
one-time, session, approval-exempt side effects, or, for an interactive run,
reviewed-safe phrase coverage without persistent state. If any candidate
remains uncovered and persistent state was unavailable, completion SHALL
return terminal `ApprovalStoreUnavailable` instead of a prompt.

`ToolApprovalAttempt` SHALL remain owner of one-time invocation state.
`ToolApprovalActor` SHALL remain owner of session and persistent grants. The
session pipeline SHALL remain owner of pending requests, response validation,
stale-response rejection, and recovery.

The implementation SHALL reuse current execution, decision, candidate, match,
and prompt-context types when they can carry the required fact. It SHALL remove
superseded overlap. A new type SHALL exist only for the actor batch protocol or
a fact that no current type represents.

#### Scenario: One actor snapshot covers every candidate

- **GIVEN** a compound command has four candidates
- **WHEN** preflight completes without a terminal result
- **THEN** the executor sends one batch request containing four stable IDs
- **AND** the actor returns one result from one grant snapshot
- **AND** no synchronous policy service reads the approval store directly

#### Scenario: Independent coverage survives unavailable persistence

- **GIVEN** the persistent store is unavailable
- **AND** interactive approval capability is available
- **AND** session and reviewed-safe coverage jointly cover every candidate
- **WHEN** completion evaluates the actor result
- **THEN** the call is allowed
- **AND** no persisted grant is assumed

#### Scenario: Reviewed-safe policy does not grant headless authority

- **GIVEN** interactive approval capability is unavailable
- **AND** a complete candidate is in the reviewed-safe catalog
- **WHEN** no one-time, session, or persistent grant covers that candidate
- **THEN** the candidate remains uncovered
- **AND** the caller follows the current unsupported-channel denial path

#### Scenario: Explicit grant covers a headless candidate

- **GIVEN** interactive approval capability is unavailable
- **AND** a session or persistent grant covers a complete candidate
- **WHEN** completion evaluates the call
- **THEN** the explicit grant covers that candidate
- **AND** reviewed-safe policy adds no authority

#### Scenario: Uncovered candidate fails closed when persistence is unavailable

- **GIVEN** the persistent store is unavailable
- **AND** one candidate remains uncovered after one-time, session, and safe
  coverage
- **WHEN** completion evaluates the call
- **THEN** it denies with `ApprovalStoreUnavailable`
- **AND** it does not offer an approval prompt

#### Scenario: Hard deny terminates before actor match

- **GIVEN** a stored grant could match a command phrase
- **AND** canonical analysis matches hard deny
- **WHEN** preflight evaluates the call
- **THEN** it returns terminal deny
- **AND** the executor sends no grant-match request

#### Scenario: Noninteractive trust zone precedes approval matching

- **GIVEN** interactive approval is unavailable
- **AND** a stored grant covers the command phrase
- **WHEN** canonical path facts fall outside the configured trust zone
- **THEN** preflight returns terminal deny
- **AND** neither the stored grant nor safe policy can override it

#### Scenario: Recovery re-evaluates the original request

- **GIVEN** a pending approval is recovered after daemon restart
- **WHEN** the response resumes the request
- **THEN** policy re-evaluates the original source and immutable context
- **AND** it obtains a current actor snapshot before execution
- **AND** it does not replay a stale allow result

### Requirement: Candidate coverage composes authorization sources

Every ShellSyntaxTree command occurrence SHALL receive a stable call-local
candidate ID. Coverage SHALL begin `Uncovered` and MAY transition once to
OneTime, Session, PersistentGlobal, PersistentFolder, ReviewedSafePolicy, or
Denied.

A stage SHALL refine only uncovered candidates. A denial SHALL be terminal.
The coordinator SHALL allow only when every candidate has non-deny coverage and
every call-level invariant passes.

Expected unresolved shell syntax MAY produce a one-time prompt without
reusable choices. An internal exception, invalid enum, duplicate candidate ID,
mismatched actor result, or impossible transition SHALL produce terminal deny.

#### Scenario: Grants and safe policy compose

- **GIVEN** a command has `cd`, `gh api`, `wc`, and `head` candidates
- **AND** global grants cover `cd` and `gh api`
- **AND** reviewed safe policy covers `wc` and `head`
- **WHEN** the coordinator completes policy
- **THEN** every candidate has coverage
- **AND** the call is allowed without a prompt

#### Scenario: One uncovered candidate prompts

- **GIVEN** three candidates are covered and one remains uncovered
- **WHEN** no strict call-level invariant denies the call
- **THEN** the call requires one interactive prompt
- **AND** the prompt identifies the uncovered candidate

#### Scenario: Internal evaluator failure denies

- **WHEN** any policy stage throws or returns an invalid typed result
- **THEN** the final result is terminal deny with `InternalPolicyFailure`
- **AND** no approval prompt can override it
- **AND** the shell does not execute

#### Scenario: One-time approval requires the exact approval-key set

- **GIVEN** the invocation attempt contains one-time approval keys
- **WHEN** the current phrase-and-effective-directory key set differs by any
  missing or extra key
- **THEN** one-time coverage is not applied
- **AND** actor-owned session or persistent coverage is unaffected

### Requirement: Causal approval intent is separate from execution scope

The system SHALL keep canonical execution facts unchanged. For Bash only, it
MAY derive approval intent from a leading ShellSyntaxTree 0.3.4 occurrence.
That occurrence SHALL publish `ChangesOnSuccess(Exact(target))`. Its next
top-level action SHALL be success-gated with `&&`.

Intent MAY continue through later top-level diagnostic statements until a
later directory mutation, differing control-flow join, alternate branch,
subshell/group boundary, dynamic flow, or unsupported region invalidates it. An
exact later success-gated `ChangesOnSuccess` effect SHALL replace intent.
`Unchanged` SHALL preserve intent. `Unknown` or a non-exact change target SHALL
invalidate intent. Causal and temporary-scope policy SHALL NOT identify
directory-transition verbs.

An intent target SHALL be eligible only when exact, absolute, normalized, and
allowed by protected-path policy. It SHALL contain no symlink segment. Every
possible fallback directory SHALL meet the same rule. A captured platform
temporary alias and its descendants MAY map to its canonical root. POSIX hosts
MAY also capture the conventional `/tmp` alias. No other symlink target SHALL
be eligible. The directory-transition candidate and first non-navigation
action on its success edge SHALL already have one-time, session, or
stored-grant coverage. Safe policy alone SHALL NOT create causal intent.

Only a reviewed diagnostic candidate without a file-writing
redirect MAY consume eligible intent. Hard deny, protected paths, folder
grants, noninteractive authority, and process execution SHALL use real facts.
The system SHALL NOT rewrite source, arguments, cwd, or model history.

Native PowerShell SHALL remain strict in this slice and SHALL NOT derive causal
scope from `Set-Location`.

#### Scenario: Exact D03 chain composes under intended tmp scope

- **GIVEN** global grants cover `cd` and `gh api`
- **AND** `wc` and `head` are reviewed diagnostic entries
- **WHEN** the agent submits
  `cd /tmp && gh api repos/example/project/actions/jobs/123456/logs > slopwatch.log 2>&1; wc -c slopwatch.log; head -100 slopwatch.log`
- **THEN** real redirect and path facts pass deny policy
- **AND** the exact protected-path-safe `/tmp` target is eligible approval
  intent for `wc` and `head`
- **AND** all four candidate coverages compose to allow

#### Scenario: Later unknown directory mutation invalidates intent

- **GIVEN** intent is `/tmp`
- **WHEN** a later `cd "$1"` precedes a diagnostic tail
- **THEN** the tail has unknown intent
- **AND** safe policy cannot use the earlier `/tmp` intent

#### Scenario: Parser-owned wrappers establish and replace intent

- **WHEN** Bash reports `ChangesOnSuccess(Exact("/tmp"))` for `command cd /tmp`
- **THEN** the effect can establish causal intent
- **AND** no Netclaw command-name rule is consulted

#### Scenario: Directory-stack effect invalidates intent

- **GIVEN** intent is `/tmp`
- **WHEN** a later `pushd` or `popd` occurrence reports `Unknown`
- **THEN** no later diagnostic receives the earlier intent

#### Scenario: Failure-only transition shape does not create intent

- **WHEN** Bash reports `Unchanged` for `cd /tmp extra`
- **THEN** no causal intent is created
- **AND** Netclaw does not reinterpret the command's private arguments

#### Scenario: Arbitrary symlink target cannot create intent

- **GIVEN** `/work/alias` is a symlink to another directory
- **WHEN** source starts with `cd /work/alias && inspect`
- **THEN** no causal approval intent is eligible
- **AND** the captured platform temporary alias remains a separate bounded
  exception

#### Scenario: Earlier symlink target cannot become a fallback

- **GIVEN** an earlier exact intent target crosses a symlink
- **WHEN** a later eligible transition replaces intent
- **THEN** the earlier target fails fallback eligibility
- **AND** no later diagnostic receives reviewed-safe intent coverage

#### Scenario: Protected fallback denial stays terminal

- **GIVEN** an earlier fallback alias resolves into a protected directory
- **WHEN** a later intent candidate also fails symlink eligibility
- **THEN** protected-path policy denies before the eligibility check
- **AND** the system does not offer a one-time approval prompt

#### Scenario: Conventional macOS tmp alias remains eligible

- **GIVEN** the host runtime temp root differs from `/tmp`
- **AND** the POSIX `/tmp` alias resolves to `/private/tmp`
- **WHEN** intent targets `/tmp` or one of its safe descendants
- **THEN** causal policy validates the canonical `/private/tmp` path
- **AND** arbitrary POSIX symlink aliases remain strict

#### Scenario: Session and folder grants use real prerequisite scope

- **GIVEN** session or persistent-folder authority covers each prerequisite
- **WHEN** causal policy checks a diagnostic tail
- **THEN** prerequisite coverage can establish intent
- **AND** a folder grant matches only the prerequisite's real scope
- **AND** intent scope cannot convert a folder near miss into coverage

#### Scenario: Alternate branch does not leak intent

- **WHEN** source is `cd /tmp && inspect || recover; head result.log`
- **THEN** the joined intent before `head` is unknown
- **AND** real execution facts still control path policy

#### Scenario: Subshell intent does not escape

- **WHEN** source is `(cd /tmp && inspect); head result.log`
- **THEN** `/tmp` intent applies only inside the subshell
- **AND** it does not cover the outer `head`

#### Scenario: Native PowerShell stays strict

- **WHEN** native Windows PowerShell analyzes
  `Set-Location C:\\Temp; Get-Content result.log`
- **THEN** no synthetic causal scope is created
- **AND** existing real-scope and provider rules decide the call

### Requirement: Shell approval decision trace is bounded and redacted

The coordinator SHALL return one ordered trace. Rows SHALL contain only enum
stage, enum outcome, enum reason, call-local candidate ID, bounded executable
basename, coverage kind, scope relation, and grant timestamp.

The trace SHALL NOT contain full commands, argument values, environment values,
redirect bodies, raw paths, tokens, secrets, or model content. It SHALL contain
at most one row per stage per candidate and 256 rows total. Text fields SHALL
contain at most 128 UTF-16 code units. Control, newline, bidi, and invalid
Unicode SHALL be escaped. Secret-pattern redaction SHALL run before logging.

Trace overflow SHALL add one `TraceTruncated` row without changing the decision.
The trace SHALL not enter prompts or session persistence. Near-miss diagnostics
SHALL project from the trace without another grant scan.

#### Scenario: Grant and safe coverage produce one trace

- **WHEN** actor grants and safe policy jointly cover a call
- **THEN** the trace contains one coverage row for each candidate
- **AND** the final row is `Allow(AllCandidatesCovered)`

#### Scenario: Malicious text cannot forge trace lines

- **GIVEN** authored input contains CR, LF, bidi controls, or a token-like secret
- **WHEN** a strict result is logged
- **THEN** controls are escaped and secrets are redacted
- **AND** no authored text creates an additional log row

#### Scenario: Trace overflow does not widen authority

- **WHEN** trace evidence exceeds a configured bound
- **THEN** later detail is replaced by `TraceTruncated`
- **AND** candidate coverage and the final decision are unchanged

### Requirement: Exact sanitized beta approval catalog

The change SHALL contain `evidence/approval-matrix.json` with exact sanitized
D01-D18 commands, observed responses, classifications, owners, parser facts,
and policy outcomes. It SHALL match the paired ShellSyntaxTree artifact
byte-for-byte.

The shared catalog SHALL NOT imply structured trace fields through prose.
Netclaw SHALL also contain `evidence/netclaw-policy-fixtures.json` with exact
candidate IDs, typed phrases, scopes, available grants and safe entries,
expected per-candidate coverage, ordered trace rows, and final outcome for the
policy-owned acceptance cases. Tests SHALL load those fields directly and
SHALL NOT branch on Dxx identifiers to manufacture expected results.

Fixture defaults SHALL explicitly provide:

- tool name, audience, and approval mode;
- interactive capability;
- session identity and safe root;
- project safe root and inherited cwd;
- persistent-store status; and
- a fixed clock.

Each case SHALL provide canonical shell environment and initial cwd. Parser facts SHALL use
command indexes. Policy facts SHALL use stable candidate IDs. Every stored
grant SHALL carry a canonical shell tag. D02, D03, D07, D08, D09, D10, D11,
D14, D17, and D18 SHALL be exact executable fixtures. Tests SHALL deserialize
the schema through source-generated `System.Text.Json` metadata and reject
unknown members.

Additional adversarial rows SHALL cover dynamic identity, deny-only wrappers,
redirects, protected paths, prefix collisions, runtime iterators, PowerShell
providers, and unsafe catalog entries.

#### Scenario: Every harvested prompt appears once

- **WHEN** the catalog loads
- **THEN** IDs D01 through D18 each occur exactly once
- **AND** each classification is correct prompt, Netclaw policy defect,
  ShellSyntaxTree fact gap, or irreducibly dynamic

#### Scenario: Catalog contains no source identity

- **WHEN** the PII audit scans the change
- **THEN** it finds no local username, private repository, channel, thread,
  host, email, token, or secret

#### Scenario: Unsafe catalog counterexamples stay strict

- **WHEN** policy evaluates `find . -exec rm {} +`,
  `awk 'BEGIN { system("touch marker") }'`, `rg --pre helper pattern .`, and
  `sort -o output input`
- **THEN** none receives reviewed safe-policy coverage
