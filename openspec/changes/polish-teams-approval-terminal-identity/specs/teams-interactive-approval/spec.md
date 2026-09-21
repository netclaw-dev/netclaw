## Purpose

Define safe, accurate terminal presentation for Teams interactive approvals
without expanding the authority of the user who selected an approval action.

## ADDED Requirements

### Requirement: Teams terminal cards show a bounded presenter label

When Teams supplies a human-readable sender label with an accepted approval
action, the system SHALL show the bounded, sanitized label in the `Approved
By` or `Denied By` terminal-card field. When the label is absent or unsafe, the
field SHALL be exactly `Authorized operator`. The label SHALL be presentation
only: the system SHALL continue to use the canonical Teams sender identifier
for authorization, requester matching, routing, and persistence. The system
SHALL NOT expose a raw sender identifier, AAD object identifier, conversation
identifier, tenant identifier, activity identifier, correlation identifier, or
nonce as the presenter label.

#### Scenario: Valid display label appears on a granted card

- **GIVEN** an authorized requester selects an approval option with a valid
  Teams display label
- **WHEN** the approval is accepted
- **THEN** the granted terminal card shows that bounded label in `Approved By`
- **AND** authorization uses the same canonical sender identifier as an action
  with no display label

#### Scenario: Valid display label appears on a denied card

- **GIVEN** an authorized requester selects Deny with a valid Teams display
  label
- **WHEN** the denial is accepted
- **THEN** the denied terminal card shows that bounded label in `Denied By`
- **AND** the requested operation is not executed

#### Scenario: Missing or unsafe label falls back safely

- **GIVEN** an accepted approval action has no label or a label with a control
  or formatting character
- **WHEN** the terminal card is rendered
- **THEN** its presenter field is exactly `Authorized operator`
- **AND** no supplied label is logged or stored alongside the canonical sender
  identifier

### Requirement: Granted terminal cards describe approval, not execution

A granted Teams terminal card SHALL show `Execution State: Execution Approved`.
Its visible and screen-reader text SHALL state only that approval was recorded
and execution was authorized; it SHALL NOT state or imply that the tool ran,
succeeded, completed, or returned a result.

#### Scenario: Approval is recorded before execution result exists

- **GIVEN** a requester selects an approval option
- **WHEN** the approval terminal card is rendered
- **THEN** it contains `Execution State: Execution Approved`
- **AND** its visible and screen-reader text contains no success or completion
  claim for the requested operation

### Requirement: Terminal-card presentation preserves approval semantics

The Teams terminal-card presentation SHALL NOT alter the existing action
binding, authorization, decision, expiry, replacement-card, or replay rules.

#### Scenario: Deny remains terminal and non-executing

- **GIVEN** a pending Teams approval
- **WHEN** the authorized requester selects Deny
- **THEN** the result is a terminal denied card
- **AND** no operation executes

#### Scenario: Approval, expiry, and replay behavior stays unchanged

- **GIVEN** a pending Teams approval
- **WHEN** the requester approves once, an old card expires, or a duplicate
  action is replayed
- **THEN** approval is granted exactly once, expiry produces one replacement
  card, and replay remains neutral without another execution
