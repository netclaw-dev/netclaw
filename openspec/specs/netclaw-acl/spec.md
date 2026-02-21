# netclaw-acl Specification

## Purpose

Define ACL evaluation semantics for channel, sender, mention policy, and tool
grants.

## Requirements

### Requirement: Channel and sender allow checks

The system SHALL evaluate channel and sender policy before turn dispatch.

#### Scenario: Sender allowed, channel allowed

- **GIVEN** sender and channel are explicitly allowed
- **WHEN** a message arrives
- **THEN** ACL evaluation returns allow

#### Scenario: Sender disallowed

- **WHEN** sender is not allowed by policy
- **THEN** ACL evaluation returns deny

### Requirement: Mention and ambient mode behavior

The system SHALL respect `require_mention` per channel.

#### Scenario: Mention-required channel without mention

- **GIVEN** channel has `require_mention=true`
- **WHEN** message has no mention
- **THEN** no model turn is dispatched

#### Scenario: Ambient channel trigger

- **GIVEN** channel has `require_mention=false`
- **WHEN** policy allows acting on the message
- **THEN** the system starts a new thread session rooted at trigger message

### Requirement: Tool and data grants

The system SHALL enforce explicit grants for tool/data access.

#### Scenario: Missing grant blocks tool call

- **WHEN** a tool call is attempted without grant
- **THEN** execution is denied with a policy reason code
