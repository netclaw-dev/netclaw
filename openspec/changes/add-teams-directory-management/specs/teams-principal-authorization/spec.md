## Purpose

Define default-deny Teams user and Entra group authorization that preserves
legacy channel behavior until an operator deliberately configures a principal
restriction.

## ADDED Requirements

### Requirement: Teams supports global and per-channel user/group principals

Teams configuration SHALL accept additive global allowed user and group IDs and
structured per-channel access overrides with canonical team ID, canonical
channel ID, allowed user IDs, and allowed group IDs. It SHALL continue to read
existing team, channel, user, and audience configuration. No authorization
entry SHALL depend on concatenated or delimiter-encoded identifiers.

#### Scenario: Existing user-only channel configuration remains valid

- **GIVEN** a Teams configuration that contains only existing allow-list and audience fields
- **WHEN** it is loaded after principal authorization is introduced
- **THEN** the configuration is accepted
- **AND** its existing access behavior is preserved

### Requirement: Channel principal restrictions are an explicit union

After the existing Teams scope, tenant, team, channel, root, mention, and
audience gates pass, channel authorization SHALL allow a sender when it matches
an applicable global explicit user, applicable global group membership,
matching per-channel explicit user, or matching per-channel group membership.
If no applicable user or group restriction is configured, the channel SHALL
preserve legacy behavior. If any applicable principal restriction is configured
and no branch matches, the channel SHALL deny the sender.

#### Scenario: Explicit user bypasses Graph membership

- **GIVEN** a channel restriction contains the sender's canonical user ID
- **WHEN** that sender posts an otherwise permitted channel message
- **THEN** the message is authorized without a Graph membership request

#### Scenario: Matching group authorizes a sender

- **GIVEN** an otherwise permitted channel has an applicable allowed group
- **WHEN** the sender is verified as a member of that group
- **THEN** the message is authorized
- **AND** its principal is trusted internal

#### Scenario: Applicable restriction with no match denies

- **GIVEN** an otherwise permitted channel has at least one applicable user or group restriction
- **WHEN** the sender matches none of those principals
- **THEN** the message is denied with `teams_group_membership_not_allowed`

### Requirement: Group membership verification is constrained and fail closed

The system SHALL verify group membership only for the requested sender and the
deduplicated applicable candidate group IDs. A membership request SHALL contain
at most 20 IDs, return as soon as a positive candidate is found, and use its
bounded cached result when available. Timeout, unavailable service,
unauthorized response, malformed result, and throttling after bounded retry
SHALL deny with the stable reason `teams_group_membership_unavailable` and
SHALL NOT include a raw ID, token, or service response in the reason.

#### Scenario: More than twenty groups are checked safely

- **GIVEN** applicable principal configuration contains more than twenty unique group IDs
- **WHEN** a sender requires group verification
- **THEN** the system checks deduplicated IDs in requests of at most twenty
- **AND** it stops once a positive group is found

#### Scenario: Membership service failure fails closed

- **GIVEN** a channel relies on allowed group membership
- **WHEN** the membership verification operation becomes unavailable or times out
- **THEN** the message is denied with `teams_group_membership_unavailable`
- **AND** no turn, binding, or approval authority is created

### Requirement: Direct messages use global principals only

Teams direct messages SHALL require `AllowDirectMessages` and a matching global
explicit user or verified global group membership. Per-channel access overrides
SHALL NOT authorize direct messages. Empty global user and group lists SHALL
deny direct messages even when direct messages are enabled.

#### Scenario: Channel override does not authorize a DM

- **GIVEN** direct messages are enabled and only a channel override grants the sender
- **WHEN** the sender sends a direct message
- **THEN** the direct message is denied

#### Scenario: Empty global principals deny a DM

- **GIVEN** direct messages are enabled with empty global user and group lists
- **WHEN** any sender sends a direct message
- **THEN** the direct message is denied

### Requirement: Trusted internal status requires a verified principal

Teams SHALL classify a sender as trusted internal only after an explicit user
match or a verified allowed-group membership. Team membership, channel
membership, a friendly name, and presence in an allowed channel SHALL NOT by
themselves create trusted internal status.

#### Scenario: Channel allow-list alone is not a trusted principal

- **GIVEN** a sender passes the configured team and channel gates without an explicit user or group restriction
- **WHEN** the channel message is accepted under legacy behavior
- **THEN** its principal remains untrusted external
