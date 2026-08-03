## MODIFIED Requirements

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
