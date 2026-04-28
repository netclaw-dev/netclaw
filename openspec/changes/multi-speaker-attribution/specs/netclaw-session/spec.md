## ADDED Requirements

### Requirement: Persist adopted-context audit records

When an authorized threaded turn adopts unsynced prior thread messages, the
session system SHALL durably persist or reuse an adopted-context record for
audit before enqueueing the authorized turn.

The persisted record SHALL include at minimum:

- session or thread identity
- authorizer identity for the current authorized message
- sync lower bound and upper bound
- included message ids
- included message timestamps
- included message sender ids
- authority-at-inclusion for each included message
- the canonical attribution projection presented to the model
- enqueue outcome or equivalent execution linkage for the authorized turn

The idempotency basis for this record SHALL be the current authorized message
identity within the session or thread. If the same authorized message is
retried or replayed after adopted-context persistence has already succeeded, the
session SHALL reuse the existing adopted-context record and update its enqueue
outcome or execution linkage rather than persist a duplicate record.

If the authorized message has no unsynced adopted gap, the session SHALL NOT
persist an adopted-context record and SHALL treat the turn as an ordinary
authorized turn.

If adopted-context persistence fails, the system SHALL NOT enqueue the
authorized turn and SHALL NOT advance the authorized-sync watermark.

If enqueue fails after the adopted-context record has been persisted, the system
SHALL leave the watermark unchanged and SHALL treat the persisted record as a
non-executed audit artifact rather than proof that the turn ran.

#### Scenario: Adopted-context record persisted for authorized turn

- **GIVEN** an authorized threaded message adopts three unsynced prior messages
- **WHEN** the turn is prepared
- **THEN** the session persists one adopted-context audit record
- **AND** the record contains the authorizer, sync bounds, included messages,
  authority-at-inclusion, canonical projection, and enqueue linkage

#### Scenario: Persistence failure blocks enqueue

- **GIVEN** an authorized threaded message would adopt unsynced prior messages
- **WHEN** adopted-context persistence fails
- **THEN** the authorized turn is not enqueued
- **AND** the authorized-sync watermark does not advance

#### Scenario: Enqueue failure leaves audit without execution

- **GIVEN** the adopted-context record has been persisted
- **WHEN** authorized turn enqueue fails
- **THEN** the watermark remains unchanged
- **AND** the persisted adopted-context record is treated as a non-executed
  audit artifact

#### Scenario: Same authorized message retry reuses persisted record

- **GIVEN** an adopted-context record already exists for a specific current
  authorized message identity
- **AND** a prior enqueue attempt for that message did not complete
- **WHEN** the system retries that same authorized message
- **THEN** the existing adopted-context record is reused
- **AND** the execution linkage is updated without persisting a duplicate

### Requirement: Adopted context is non-executable quoted context

The session SHALL treat adopted-context material as quoted context rather than
ordinary authoritative turn history unless a later explicit change says
otherwise. Only the current authorized message in that turn SHALL be executable.

Adopted or pending unauthorized content SHALL NOT directly:

- dispatch a model turn on its own;
- enter slash-command dispatch;
- originate tool approvals;
- originate tool calls, reminders, or jobs; or
- originate direct durable memory writes.

#### Scenario: Adopted context cannot execute without current authorized message

- **GIVEN** a thread contains only unauthorized pending messages after the last
  watermark
- **WHEN** no authorized message arrives
- **THEN** the session does not execute a turn from that pending material

#### Scenario: Authorized turn executes only current message

- **GIVEN** an authorized turn includes adopted context plus the current
  authorized message
- **WHEN** the session executes the turn
- **THEN** only the current authorized message is treated as executable
- **AND** the adopted context remains quoted supporting material

### Requirement: Canonical projection is derived from persisted record

The model-visible multi-speaker projection SHALL be derived from the persisted
adopted-context record, not reconstructed ad hoc from raw thread history after
the fact.

If no adopted-context record exists because the turn had no unsynced gap, the
model SHALL receive only the current authorized message and no empty
adopted-context projection.

#### Scenario: Audit replay matches model-visible projection

- **GIVEN** an adopted-context record exists for a turn
- **WHEN** an operator reviews that turn later
- **THEN** the stored canonical projection matches the attribution framing that
  was shown to the model
