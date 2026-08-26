## ADDED Requirements

### Requirement: Per-channel mention rule on the channel detail leaf

The channel detail leaf (`ChannelsConfigScreen.EditAudience`) SHALL show and edit the
channel's `MentionRequiredInThread` value next to the audience question. The leaf
therefore holds two per-channel settings: the audience and the mention rule. Toggling
the mention rule SHALL persist immediately, the same way an audience change persists.

The value is per-channel. There SHALL be no connector-wide or workspace-wide control
for this rule in the TUI.

#### Scenario: The channel detail leaf shows the mention rule

- **GIVEN** the operator opens the detail leaf for a configured channel
- **WHEN** the leaf renders
- **THEN** it shows the channel's audience question and its `MentionRequiredInThread` value

#### Scenario: Toggling the mention rule persists immediately

- **GIVEN** the operator is on the channel detail leaf with `MentionRequiredInThread` off
- **WHEN** the operator toggles it on and confirms
- **THEN** the channel's per-channel `MentionRequiredInThread` value is saved as `true`
- **AND** the save behaves like every other channel edit

### Requirement: Add-time seed of the mention rule from the channel audience

When a channel is added, the add-channel flow SHALL seed `MentionRequiredInThread`
from the audience the channel receives at add time. For a `Team` or `Public` audience,
the flow SHALL write `MentionRequiredInThread = true`. For a `Personal` audience, and
for a DM, the flow SHALL leave the value off. This is a write-time default; the operator
MAY change it later on the channel detail leaf.

#### Scenario: A team or public channel is seeded on

- **GIVEN** the deployment posture default audience is `Team`
- **WHEN** the operator adds a new channel
- **THEN** the channel is added at the `Team` audience
- **AND** its `MentionRequiredInThread` value is seeded `true`

#### Scenario: A personal channel is seeded off

- **GIVEN** a channel is added at the `Personal` audience
- **WHEN** the add flow writes the channel entry
- **THEN** `MentionRequiredInThread` is left off for that channel

#### Scenario: The operator can change the seeded value later

- **GIVEN** a channel was seeded `MentionRequiredInThread = true` at add time
- **WHEN** the operator opens the channel detail leaf and toggles it off
- **THEN** the channel's value is saved as off
- **AND** the write-time seed does not re-apply
