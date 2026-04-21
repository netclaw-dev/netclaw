## MODIFIED Requirements

### Requirement: Channel approval capability

Channels SHALL declare whether they support interactive approval via a
capability flag. When a tool requires approval and the active channel does NOT
support it, the system SHALL immediately deny the tool with reason
`channel_does_not_support_approval`. The system SHALL NOT hang or timeout.

Channels that support interactions (including Discord) SHALL prefer interaction
rendering for approval prompts and SHALL provide a deterministic text fallback
path with equivalent decision options when interactions are unavailable.

#### Scenario: Unsupported channel auto-denies

- **GIVEN** the headless channel (no interactive user)
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes `shell_execute`
- **THEN** the tool is immediately denied with
  `channel_does_not_support_approval`

#### Scenario: Slack channel renders approval prompt

- **GIVEN** the Slack channel (supports interactive approval)
- **AND** `shell_execute` is in Approval mode
- **WHEN** the agent invokes an unapproved `shell_execute` command
- **THEN** the channel renders the approval prompt as Block Kit buttons

#### Scenario: Discord channel renders interaction prompt when available

- **GIVEN** the Discord channel supports interactive approval
- **AND** Discord interaction callbacks are available
- **WHEN** the agent invokes an unapproved `shell_execute` command
- **THEN** the channel renders the approval prompt using Discord interactions
- **AND** selected option is routed as `ToolInteractionResponse`

#### Scenario: Discord channel falls back to deterministic text options

- **GIVEN** the Discord channel supports interactive approval
- **AND** Discord interaction callbacks are unavailable
- **WHEN** the agent invokes an unapproved `shell_execute` command
- **THEN** the channel renders a deterministic A/B/C/D text approval prompt
- **AND** text replies map to equivalent approval decisions without timeout-based ambiguity
