## Purpose

Ensure that a trusted Teams approval Action.Execute callback reaches its
matching pending approval and returns an accurate terminal response card.

## ADDED Requirements

### Requirement: Teams action callbacks bind to their source approval

The system SHALL accept a Teams approval action only when its authenticated
tenant, chat or channel destination, conversation scope, root locator,
requester, correlation, source-card locator, nonce, and offered action match
one pending approval. The system SHALL support Personal, Posts, and Threads
destinations without weakening any binding check.

#### Scenario: Matching approval action in a channel post

- **WHEN** the requester invokes an offered action from the card for a pending Posts approval
- **THEN** the system SHALL submit that action to the shared approval authority and return its terminal result

#### Scenario: Matching approval action in a channel thread

- **WHEN** the requester invokes an offered action from the card for a pending Threads approval
- **THEN** the system SHALL bind it to the original thread root and return its terminal result

#### Scenario: Callback with a different source-card locator

- **WHEN** an action uses a source-card locator that differs from the pending approval card
- **THEN** the system SHALL reject the action without submitting an approval decision

### Requirement: Teams action rejections are safely attributable

The system SHALL record exactly one fixed, non-sensitive reason code when a
Teams approval action is rejected by a validation gate. The reason code SHALL
be one of `approval_action_session_identity_invalid`,
`approval_action_session_mismatch`, `approval_action_destination_invalid`,
`approval_action_key_invalid`, `approval_action_correlation_not_found`,
`approval_action_prompt_locator_mismatch`, `approval_action_nonce_mismatch`,
`approval_action_wrong_requester`, `approval_action_option_not_offered`, or
`approval_action_retry_mismatch`. The system SHALL NOT record raw activity,
tenant, conversation, requester, correlation, nonce, or card-content values
in this attribution.

#### Scenario: Invalid action key

- **WHEN** a callback has an action key that is not supported by the approval card protocol
- **THEN** the system SHALL reject it and record `approval_action_key_invalid`

#### Scenario: Action by a different requester

- **WHEN** a callback is authenticated as a requester other than the one that owns the pending approval
- **THEN** the system SHALL reject it and record `approval_action_wrong_requester`

### Requirement: Teams approval results remain authoritative and idempotent

The system SHALL return the terminal Teams card that matches the authoritative
approval outcome. A valid deny action SHALL return Denied, not Rejected. An
expired approval SHALL return Expired and create exactly one reissued approval
with a fresh nonce. A duplicate, replayed, or no-longer-pending callback SHALL
not invoke the shared approval authority again and SHALL return a neutral
terminal response.

#### Scenario: Valid deny action

- **WHEN** the requester invokes the offered deny action for a pending approval
- **THEN** the system SHALL return a Denied terminal card

#### Scenario: Expired approval action

- **WHEN** the requester invokes an offered action after the approval expires
- **THEN** the system SHALL return an Expired terminal card and create one fresh approval card

#### Scenario: Replayed approval action

- **WHEN** a callback repeats an action that has already reached a terminal result
- **THEN** the system SHALL return a neutral terminal card and SHALL NOT submit another decision
