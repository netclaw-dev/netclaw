# tool-approval-gates Specification

## Purpose

Define the general-purpose tool approval gate system that intercepts tool
invocations and requires interactive user sign-off before execution. Covers
per-audience approval configuration, configurable hard deny lists, shell
command pattern matching, mid-turn approval pause, the ToolInteraction
request/response protocol, persistent approval storage, and channel
approval capability.

## Requirements

### Requirement: Tool approval configuration per audience

The system SHALL support per-audience tool approval configuration via
`ToolApprovalConfig` on `ToolAudienceProfile`. Each audience profile SHALL
independently specify a `DefaultMode` (Auto, Approval, Deny) and per-tool
overrides in `ToolOverrides`.

Approval mode resolution SHALL use deterministic precedence:

1. Matcher-derived approval-mode key override (for example
   `file_write:control-plane`)
2. Base tool key override (for example `file_write`)
3. Matcher fail-closed behavior for Personal audience
4. Audience `DefaultMode`

Runtime audience defaults SHALL NOT implicitly place `shell_execute` in
`Approval` mode. Instead, the init-generated Personal config SHALL explicitly
write `ApprovalPolicy.ToolOverrides.shell_execute = Approval` as the
recommended shell-safe default.

#### Scenario: Shell requires approval in init-generated Personal config

- **GIVEN** a Personal audience session whose generated config explicitly sets
  `ApprovalPolicy.ToolOverrides.shell_execute` to `Approval`
- **WHEN** the agent invokes `shell_execute`
- **THEN** the system checks the approval cache before execution
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
- **THEN** the system checks approval cache and may prompt
- **AND** when a Team session invokes `shell_execute`
- **THEN** the system denies immediately without prompting

#### Scenario: Matcher-specific override key takes precedence over base tool key

- **GIVEN** `ApprovalPolicy.ToolOverrides.file_write = Auto`
- **AND** `ApprovalPolicy.ToolOverrides.file_write:control-plane = Approval`
- **WHEN** the agent invokes `file_write` targeting a control-plane path
- **THEN** the resolved mode is `Approval`
- **AND** the call is approval-gated unless already approved for that path pattern

#### Scenario: Base tool key applies when matcher-specific key is absent

- **GIVEN** `ApprovalPolicy.ToolOverrides.file_write = Approval`
- **AND** no override exists for `file_write:control-plane`
- **WHEN** the agent invokes `file_write` targeting a control-plane path
- **THEN** the resolved mode is `Approval`
- **AND** mode resolution does NOT fall directly to `DefaultMode`

### Requirement: Configurable hard deny list

The system SHALL enforce shell hard-deny composition across both
operation-level hard deny and resource-level hard deny:

- **Operation hard-deny**: command intent patterns evaluated by
  `ShellCommandPolicy` (for example self-destructive/system-destructive verbs)
- **Resource hard-deny**: protected-path checks evaluated by `ToolPathPolicy`

Shell execution SHALL be denied when either hard-deny layer matches. Operation
hard-deny SHALL be evaluated first and SHALL short-circuit result reason when it
matches. Denied commands SHALL never be approvable. The system SHALL ship with
sensible defaults: commands that kill the Netclaw daemon process, `rm -rf /`,
`rm -rf ~/`, and fork bombs. Operators SHALL be able to add or remove operation
hard-deny patterns via configuration.

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

#### Scenario: Operation hard-deny reason takes precedence over resource deny

- **GIVEN** a shell command matches both operation and resource deny checks
- **WHEN** the command is evaluated
- **THEN** operation hard-deny is applied first
- **AND** the surfaced deny reason is the operation hard-deny reason

#### Scenario: Resource hard-deny blocks when operation hard-deny does not match

- **GIVEN** a shell command does not match operation hard-deny patterns
- **AND** the command references a protected file path
- **WHEN** the command is executed
- **THEN** execution is denied by resource hard-deny

### Requirement: Shell command pattern matching

The system SHALL extract verb-chain prefix patterns from shell commands using
tokenization. The verb chain SHALL consist of non-flag tokens from the start of
the command until the first flag (`-`), path, or URL argument. For compound
commands (`&&`, `||`, `;`, `|`), each segment SHALL be evaluated independently.
For `bash -c` or `sh -c` wrappers, the inner command SHALL be extracted and
scanned recursively.

#### Scenario: Verb chain extracted from simple command

- **GIVEN** the command `git push origin main`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `git push`

#### Scenario: Verb chain stops at flag

- **GIVEN** the command `ls -la /tmp`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `ls`

#### Scenario: Multi-level verb chain

- **GIVEN** the command `docker compose up -d`
- **WHEN** the pattern is extracted
- **THEN** the pattern is `docker compose up`

#### Scenario: Compound command segments evaluated independently

- **GIVEN** the command `git add . && git commit -m "fix" && git push`
- **WHEN** approval is checked
- **THEN** patterns `git add`, `git commit`, and `git push` are each checked
  independently against the approval cache

#### Scenario: Unapproved compound segments batched in one prompt

- **GIVEN** `git add` is approved but `git commit` and `git push` are not
- **WHEN** the command `git add . && git commit -m "fix" && git push` is checked
- **THEN** a single approval prompt lists both `git commit` and `git push`
- **AND** the full compound command is shown for context

#### Scenario: bash -c inner command scanned recursively

- **GIVEN** the command `bash -c "git push --force"`
- **WHEN** approval and hard deny are checked
- **THEN** the inner command `git push --force` is extracted and scanned
- **AND** pattern `git push` is checked against the approval cache

### Requirement: IToolApprovalMatcher extension point

The system SHALL define an `IToolApprovalMatcher` interface for tool-specific
pattern extraction and matching. Shell SHALL implement verb-chain matching. A
default implementation SHALL provide tool-name-level matching for tools without
a custom matcher. New tool types MAY provide their own matchers.

#### Scenario: Shell uses verb-chain matcher

- **GIVEN** a `shell_execute` tool call with command `npm install lodash`
- **WHEN** the approval system extracts the pattern
- **THEN** `ShellApprovalMatcher` extracts `npm install`

#### Scenario: MCP tool uses default matcher

- **GIVEN** an MCP tool `memorizer/store` in Approval mode
- **WHEN** the approval system extracts the pattern
- **THEN** `DefaultApprovalMatcher` extracts `memorizer/store` (the tool name)

#### Scenario: Multi-token pattern prefix matches invocation

- **GIVEN** `git push` is in the Personal approval list for `shell_execute`
- **WHEN** the agent runs `git push --tags origin main`
- **THEN** `ShellApprovalMatcher.IsApproved` returns true (prefix match on word boundary)

#### Scenario: Single-token pattern requires exact match

- **GIVEN** `gh` is in the Personal approval list for `shell_execute`
- **WHEN** the agent runs `gh pr create`
- **THEN** `ShellApprovalMatcher.IsApproved` returns false
- **AND** single-token patterns do NOT prefix-match multi-token verb chains
- **NOTE** This prevents approving `gh --help` from also approving `gh pr create`

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

The system SHALL define a general `ToolInteractionRequest` session output and
`ToolInteractionResponse` session command for channel-mediated user interactions.
The interaction `Kind` SHALL identify the interaction type (`approval` for v1).
`ToolInteractionRequest` SHALL be a lifecycle output (always delivered regardless
of `OutputFilter`).

#### Scenario: Approval request emitted as session output

- **GIVEN** a tool requires approval
- **WHEN** the pipeline detects the approval requirement
- **THEN** a `ToolInteractionRequest` with `Kind=approval` is emitted
- **AND** it includes `CallId`, `ToolName`, the command/pattern, and available
  options (approve once, approve for this chat, approve always, deny)

#### Scenario: Channel routes response back to session

- **GIVEN** a `ToolInteractionRequest` has been emitted
- **WHEN** the user selects an option (via button click, text reply, etc.)
- **THEN** the channel sends a `ToolInteractionResponse` to the session actor
- **AND** the response includes `CallId` and the selected option key

### Requirement: Approval requests originate only from the current authorized executable message

Tool approval prompts SHALL only originate from tool invocations caused by the
current authorized executable message in a turn. Adopted-context material and
pending unauthorized messages SHALL NOT directly cause approval requests.

Approval prompts and stored approval context for those requests SHALL identify
the current authorizer for the executable message. When the turn's
adopted-context window is non-empty, the prompt and stored approval context
SHALL indicate that adopted context was present for the turn and SHALL name the
adopted speakers from that window by stable sender id. When the adopted-context
window is empty, adopted-speaker provenance SHALL be omitted.

#### Scenario: Adopted unauthorized command text does not raise approval prompt

- **GIVEN** adopted context contains text asking Netclaw to run `git push`
- **AND** that text came from an unauthorized speaker
- **WHEN** the authorized turn is processed
- **THEN** no approval prompt is raised solely because of the adopted text

#### Scenario: Current authorized request can still require approval

- **GIVEN** the current authorized message asks Netclaw to run `git push`
- **WHEN** the session processes the turn
- **THEN** the tool approval gate evaluates that current authorized request
- **AND** an approval prompt may be emitted if policy requires it

#### Scenario: Approval prompt identifies authorizer and adopted-speaker provenance

- **GIVEN** the current authorized message from `U111` asks Netclaw to run a
  command in a turn whose adopted-context window includes `U222` and `U333`
- **WHEN** the tool approval gate emits a prompt
- **THEN** the prompt identifies `U111` as the current authorizer
- **AND** the prompt indicates that adopted context was present for the turn
- **AND** it names `U222` and `U333` as adopted speakers

#### Scenario: Stored approval context preserves provenance

- **GIVEN** a tool request from the current authorized message requires approval
- **AND** the turn's adopted-context window includes speaker `U222`
- **WHEN** the approval request is persisted or otherwise stored for retry or
  audit
- **THEN** the stored approval context identifies the current authorizer
- **AND** records that adopted-speaker provenance includes `U222`

#### Scenario: Empty adopted window omits adopted-speaker provenance

- **GIVEN** a tool request from the current authorized message requires approval
- **AND** the turn has no adopted-context window
- **WHEN** the tool approval gate emits and stores the approval request
- **THEN** the authorizer is still identified
- **AND** no adopted-speaker provenance field is included

### Requirement: Persistent approval storage

The system SHALL store persistent approvals ("Approve Always" decisions) in
`~/.netclaw/config/tool-approvals.json`, separate from `netclaw.json`. The file
SHALL NOT be monitored by `ConfigWatcherService`. The file SHALL contain
per-audience sections with per-tool approval lists. For shell, the lists SHALL
contain command patterns. For other tools, approval SHALL be tool-level or
matcher-pattern-level as defined by that matcher. The file SHALL be read at
startup and written immediately on "Approve Always" decisions.

The retry path for "Approve Once" SHALL match against the filtered unapproved
pattern set presented in the approval prompt, not against pre-filter pattern
candidates.

#### Scenario: Approve Always persists to file

- **GIVEN** the user clicks "Approve Always" for pattern `git push`
- **WHEN** the approval is processed
- **THEN** `git push` is added to the Personal shell_execute list in
  `tool-approvals.json`
- **AND** the daemon does NOT restart

#### Scenario: Persistent approvals loaded at startup

- **GIVEN** `tool-approvals.json` contains `{"personal":{"shell_execute":["git push"]}}`
- **WHEN** the daemon starts
- **THEN** `git push` is pre-approved for Personal audience shell commands

#### Scenario: Approve Once is retry-scoped only

- **GIVEN** the user clicks "Approve Once" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** the blocked `docker build` call is retried immediately
- **AND** a later `docker build` call in the same session prompts again
- **AND** `tool-approvals.json` is NOT modified

#### Scenario: Approve Once retry uses filtered unapproved patterns

- **GIVEN** a command yields candidate patterns where some are already approved
- **AND** the prompt shows only filtered unapproved patterns
- **WHEN** the user selects "Approve Once"
- **THEN** the immediate retry succeeds without a second prompt for that call
- **AND** the one-time bypass checks only the filtered unapproved set

#### Scenario: Approve Once for control-plane file path is path-scoped

- **GIVEN** `file_write` on `.netclaw/tooling/AGENTS.md` prompts with matcher
  pattern `file_write:control-plane:.netclaw/tooling/AGENTS.md`
- **WHEN** the user selects "Approve Once"
- **THEN** the blocked retry for that same path proceeds without reprompt
- **AND** a subsequent control-plane write to a different path prompts again
- **AND** no persistent approval file entry is written

#### Scenario: Approve For This Chat is session-scoped only

- **GIVEN** the user clicks "Approve For This Chat" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** `docker build` is approved for the current session only
- **AND** `tool-approvals.json` is NOT modified

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
- **THEN** the channel renders the approval prompt as Block Kit buttons
