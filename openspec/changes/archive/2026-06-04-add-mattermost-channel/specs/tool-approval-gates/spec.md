## MODIFIED Requirements

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
