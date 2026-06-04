## ADDED Requirements

### Requirement: Approval pause persistence carries turn context

When a tool approval prompt is emitted from a session turn, the persisted approval request SHALL carry the original turn context as a single durable context record. The approval request MAY continue to carry tool-specific prompt data, option keys, candidates, and compatibility fields, but authority-bearing session context SHALL have one canonical persisted representation for new events.

#### Scenario: Approval request persists context record

- **GIVEN** a tool call requires approval during a session turn
- **WHEN** the session persists the approval request
- **THEN** the journaled event includes the turn context for the original request
- **AND** the pending interaction restored from that event carries the same context

#### Scenario: Tool-specific prompt data remains separate

- **GIVEN** an approval request includes command patterns, candidate verbs, option keys, and directory candidates
- **WHEN** the turn context is persisted with the approval request
- **THEN** tool-specific prompt data remains separate from the turn context
- **AND** the turn context does not become a dumping ground for approval-rendering state

### Requirement: Approval responses use persisted requester context

Approval response authorization SHALL use the requester and principal from the persisted turn context for the pending approval. A recovered approval response SHALL enforce the same requester-only approval rule as the live path, unless the original requester principal represents verified automation where channel-member approval is allowed.

#### Scenario: Non-requester approval rejected after recovery

- **GIVEN** a pending approval was restored with requester `U-requester`
- **WHEN** sender `U-other` approves the prompt
- **THEN** the approval response is rejected
- **AND** the tool is not redriven

#### Scenario: Verified automation approval remains approvable by channel member

- **GIVEN** a pending approval was restored with a verified automation principal
- **WHEN** a valid channel member approves the prompt
- **THEN** the approval response is accepted according to the same rule used on the live path
- **AND** the redrive uses the original turn context
