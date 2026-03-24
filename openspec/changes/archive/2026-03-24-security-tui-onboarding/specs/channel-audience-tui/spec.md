# channel-audience-tui Specification

## Purpose

Define the interactive TUI step for per-channel audience assignment with
dynamic channel add/remove and keyboard-driven audience cycling.

## Requirements

### Requirement: Channel list with audience cycling

The wizard SHALL present a channel list where each row shows the channel
name, Slack ID, and current audience. ←/→ keys SHALL cycle the audience
value on the focused row.

#### Scenario: Cycle audience with arrow keys

- **GIVEN** the focused channel row shows audience "Team"
- **WHEN** the user presses →
- **THEN** the audience changes to "Personal"
- **AND** pressing → again changes to "Public"
- **AND** pressing → again wraps to "Team"

#### Scenario: Left arrow cycles in reverse

- **GIVEN** the focused channel row shows audience "Team"
- **WHEN** the user presses ←
- **THEN** the audience changes to "Public"

#### Scenario: Enter advances to next step

- **WHEN** the user presses Enter on the channel list
- **THEN** the wizard advances to the next step
- **AND** the current audience assignments are preserved

### Requirement: Dynamic channel adding via Slack API

The wizard SHALL allow adding channels by pressing `a`, which opens a
type-to-filter search populated from `conversations.list`.

#### Scenario: Add channel by name search

- **GIVEN** the user presses `a` on the channel list
- **WHEN** a text input appears and the user types "gen"
- **THEN** a filtered list shows channels matching "gen" (e.g., #general)
- **AND** pressing Enter on a match adds it to the channel list with the
  posture default audience

#### Scenario: Channel already in list

- **GIVEN** #general is already in the channel list
- **WHEN** the user tries to add #general again
- **THEN** the channel is not duplicated
- **AND** a status message indicates it's already added

### Requirement: Channel removal

The wizard SHALL allow removing channels by pressing `d` on the focused row.

#### Scenario: Remove channel

- **GIVEN** the focused row is #dev-ops
- **WHEN** the user presses `d`
- **THEN** #dev-ops is removed from the list
- **AND** focus moves to the nearest remaining row

#### Scenario: Cannot remove DMs row

- **GIVEN** DMs are enabled and the focused row is "DMs"
- **WHEN** the user presses `d`
- **THEN** nothing happens (DMs cannot be removed, only toggled in
  ChatServices)

### Requirement: DMs row present when enabled

The channel list SHALL include a "DMs" row when direct messages are enabled
in ChatServices. The DMs row audience is editable via ←/→.

#### Scenario: DMs row with posture default

- **GIVEN** DMs are enabled and posture is Personal
- **WHEN** the Channels step is shown
- **THEN** a "DMs" row appears with audience "Personal"

### Requirement: Skip when Slack disabled

The Channels step SHALL be skipped entirely when Slack is disabled in
ChatServices. No `ChannelAudiences` are written to config.

#### Scenario: Slack disabled skips Channels

- **GIVEN** Slack is disabled in the ChatServices step
- **WHEN** the wizard advances past SecurityPosture and ACL
- **THEN** the Channels step is skipped
- **AND** no `ChannelAudiences` section is written to config

### Requirement: Block on API failure with actionable error

If `conversations.list` fails, the Channels step SHALL display an actionable
error message and block until the user resolves the issue. No silent fallback
to manual entry.

#### Scenario: conversations.list fails with missing scope

- **GIVEN** the Slack token is valid but lacks `channels:read` scope
- **WHEN** the Channels step loads
- **THEN** an error message is shown: "Failed to list channels: missing
  channels:read scope. Add this scope to your Slack app and press Enter
  to retry."
- **AND** the user cannot advance until the API call succeeds or they
  press Esc to go back and re-enter credentials

#### Scenario: conversations.list fails with network error

- **GIVEN** the Slack API is unreachable
- **WHEN** the Channels step loads
- **THEN** an error message is shown with the failure reason
- **AND** Enter retries the API call
- **AND** Esc goes back to the previous step

### Requirement: Audience defaults from posture

Channel audiences SHALL be pre-populated based on the deployment posture
selected in the SecurityPosture step. Users can override per-channel.

#### Scenario: Posture defaults applied to new channels

- **GIVEN** posture is Team
- **WHEN** the user adds a new channel
- **THEN** the new channel's audience defaults to Team
