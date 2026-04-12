## MODIFIED Requirements

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
