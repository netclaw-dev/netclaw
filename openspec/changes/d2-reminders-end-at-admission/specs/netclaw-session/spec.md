## REMOVED Requirements

### Requirement: Reminder redelivery best-effort dedup

**Reason**: The in-memory ledger `ProcessedReminderIds`, the in-flight reservation, and the three pre-checks are deleted. One durable check at admission replaces them, and it holds across snapshot recovery. The requirement returns as "Reminder redelivery is deduplicated at admission".
**Migration**: None. The ledger was never persisted.

## ADDED Requirements

### Requirement: Reminder redelivery is deduplicated at admission

The session SHALL deduplicate a reminder fire once, at admission, by the source
key of the stored input. `SessionState.RecentSourceMessageKeys` holds the key,
and `SessionSnapshot` persists it. The session SHALL NOT keep a separate
in-memory ledger or in-flight reservation for reminder fires.

The session SHALL make the admission decision before any other side effect. On
a duplicate, the session SHALL reply `CommandAck` to the sender without
persisting events, dispatching an LLM call, or abandoning an open tool batch.
The decision SHALL apply in every phase (`Ready`, `Processing`, `Compacting`).

The check holds across snapshot recovery for the keys that the bounded
`RecentSourceMessageKeys` list still holds (256 keys).

#### Scenario: Redelivered reminder is a duplicate in Ready phase

- **GIVEN** the session is in `Ready` phase and its journal holds an input with
  source key `check-pr:1712000000000`
- **WHEN** a `SendUserMessage` arrives with
  `MessageSource.ReminderId = "check-pr:1712000000000"`
- **THEN** the session replies `CommandAck` to the sender
- **AND** it persists no event
- **AND** the LLM is not invoked

#### Scenario: Redelivered reminder is a duplicate in Processing phase

- **GIVEN** the session is in `Processing` phase and runs the turn of the fire
  `nightly-report:1712005000000`
- **WHEN** a `SendUserMessage` redelivery arrives with the same reminder ID
- **THEN** the session replies `CommandAck` without buffering the message
- **AND** the in-flight turn is unaffected

#### Scenario: A duplicate survives snapshot recovery

- **GIVEN** the session admitted a fire and then recovered from a later snapshot
- **WHEN** Akka.Reminders redelivers the fire
- **THEN** the session replies `CommandAck` and stores no second input

#### Scenario: A duplicate does not abandon an open tool batch

- **GIVEN** a recovered session holds an interrupted tool batch
- **WHEN** a redelivery of an admitted fire arrives
- **THEN** the session acknowledges it
- **AND** the journal gets no `ToolBatchAbandoned` event from it

#### Scenario: Non-reminder user messages are not deduped by reminder state

- **GIVEN** a regular `SendUserMessage` arrives with `MessageSource.ReminderId = null`
- **THEN** the message is processed normally
