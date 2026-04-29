## ADDED Requirements

### Requirement: Persist adopted-context audit records

When an authorized threaded turn adopts unsynced prior thread messages, the
session system SHALL durably persist or reuse an adopted-context record for
audit before execution continues for that authorized turn.

The persisted record SHALL include at minimum:

- session or thread identity
- authorizer identity for the current authorized message
- sync lower bound and upper bound
- included message ids
- included message timestamps
- included message sender ids
- authority-at-inclusion for each included message
- the exact canonical attribution projection presented to the model
- enough linkage to correlate retries or recovery for the same authorized
  message id

The idempotency basis for this record SHALL be the current authorized message
identity within the session or thread. If the same authorized message is
retried or replayed after adopted-context persistence has already succeeded, the
session SHALL reuse the existing adopted-context record and exact persisted
projection rather than persist a duplicate or re-derive a new projection from
raw thread history.

If the authorized message has no unsynced adopted gap, the session SHALL NOT
persist an adopted-context record and SHALL treat the turn as an ordinary
authorized turn.

If adopted-context persistence fails, the system SHALL NOT enqueue the
authorized turn and SHALL NOT advance the authorized-sync watermark.

If durable turn completion is not observed after the adopted-context record has
been persisted, the durable authorized-sync watermark SHALL remain unchanged and
the persisted record SHALL remain a fail-closed audit artifact that retries or
recovery can reuse rather than proof that the turn ran.

#### Scenario: Adopted-context record persisted for authorized turn

- **GIVEN** an authorized threaded message adopts three unsynced prior messages
- **WHEN** the turn is prepared
- **THEN** the session persists one adopted-context audit record
- **AND** the record contains the authorizer, sync bounds, included messages,
  authority-at-inclusion, and the exact canonical projection

#### Scenario: Persistence failure blocks enqueue

- **GIVEN** an authorized threaded message would adopt unsynced prior messages
- **WHEN** adopted-context persistence fails
- **THEN** the authorized turn is not enqueued
- **AND** the authorized-sync watermark does not advance

#### Scenario: Missing durable completion leaves audit without watermark advance

- **GIVEN** the adopted-context record has been persisted
- **WHEN** durable turn completion is not observed for that authorized message
- **THEN** the durable authorized-sync watermark remains unchanged
- **AND** the persisted adopted-context record is treated as a non-executed
  audit artifact that retries or recovery may reuse

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

### Requirement: Canonical projection is persisted exactly and reused

The threaded adapter MAY construct the model-visible multi-speaker projection
before session handoff. When adopted context exists, the session SHALL persist
that exact projection together with the adopted-message metadata before
execution continues.

Retries, replay, or crash recovery for the same authorized message id SHALL
reuse the persisted adopted-context record keyed by that authorized message id
rather than reconstruct a different projection from raw thread history.

If no adopted-context record exists because the turn had no unsynced gap, the
model SHALL receive only the current authorized message and no empty
adopted-context projection.

#### Scenario: Audit replay matches model-visible projection

- **GIVEN** an adopted-context record exists for a turn
- **WHEN** an operator reviews that turn later
- **THEN** the stored canonical projection matches the attribution framing that
  was shown to the model
