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

### Requirement: Persistent approval storage

The system SHALL store persistent approvals ("Approve Always" decisions) in
`~/.netclaw/config/tool-approvals.json`, separate from `netclaw.json`. The file
SHALL NOT be monitored by `ConfigWatcherService`. The file SHALL contain
per-audience sections with per-tool approval lists. For shell, the lists SHALL
contain command patterns. For other tools, approval SHALL be tool-level
(`true`). The file SHALL be read at startup and written immediately on "Approve
Always" decisions.

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

#### Scenario: Approve For This Chat is session-scoped only

- **GIVEN** the user clicks "Approve For This Chat" for pattern `docker build`
- **WHEN** the approval is processed
- **THEN** `docker build` is approved for the current session only
- **AND** `tool-approvals.json` is NOT modified
- **AND** a new session will prompt for `docker build` again

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
