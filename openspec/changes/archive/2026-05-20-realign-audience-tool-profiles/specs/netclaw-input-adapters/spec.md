## ADDED Requirements

### Requirement: Direct-message audience resolution requires an operator-vetted sender

A channel adapter resolving the audience for an inbound direct message SHALL
resolve it to the `Team` audience only when the sender is an
operator-allowlisted user or the conversation is an explicitly allowlisted
channel. A direct message from a sender who is not on the channel allow-list
SHALL resolve to the `Public` audience.

Explicit `ChannelAudiences` overrides SHALL continue to take precedence: an
operator MAY map the `dm` key, or a specific channel id, to any audience, and
that override SHALL be honored ahead of the default resolution above.

#### Scenario: DM from a non-allowlisted user resolves to Public

- **GIVEN** direct messages are enabled with an empty allowed-users list
- **AND** no `ChannelAudiences` override applies
- **WHEN** a user who is not on the allow-list sends a direct message
- **THEN** the resolved audience is `Public`

#### Scenario: DM from an allowlisted user resolves to Team

- **GIVEN** a user is on the channel allowed-users list
- **WHEN** that user sends a direct message
- **THEN** the resolved audience is `Team`

#### Scenario: dm audience override takes precedence

- **GIVEN** `ChannelAudiences["dm"]` is set to `team`
- **WHEN** a non-allowlisted user sends a direct message
- **THEN** the resolved audience is `Team` as specified by the override
