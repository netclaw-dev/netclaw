## MODIFIED Requirements

### Requirement: Hydration merges into an adopted-context window on authorized inbound

Threaded channel adapters SHALL hydrate prior thread messages only when an
authorized inbound message is about to create an executable turn. The adapter
SHALL merge unsynced prior thread messages into an explicit adopted-context
window before the current authorized message, not as separate turns and not as
ordinary live message history.

The session layer SHALL receive one authorized turn consisting of the adopted
window plus the current authorized message. The session layer SHALL NOT receive
distinct backfill turns for pending speakers.

For adoption semantics, `HasAdoptedContext` SHALL mean exactly that the adopted
window is non-empty. `HasThirdPartyAdoptedContext` SHALL be derived separately
and SHALL be true only when at least one sender id in the adopted window differs
from the current authorized sender for the executable message. Adopted-speaker
provenance SHALL include all sender ids present in the adopted window, including
self-only adopted history.

#### Scenario: Unsynced thread gap adopted only on authorized inbound

- **GIVEN** a thread contains prior unsynced messages
- **WHEN** an authorized user sends the next inbound message
- **THEN** the prior unsynced messages are hydrated into adopted context
- **AND** the current authorized message is appended as the executable message

#### Scenario: Unauthorized inbound does not trigger hydration turn

- **GIVEN** a non-allowed user sends a threaded message
- **WHEN** no authorized user is speaking on that inbound event
- **THEN** the adapter does not dispatch a hydrated turn
- **AND** the message remains pending source-thread context

#### Scenario: Self-only adopted history still counts as adopted context

- **GIVEN** the adopted window contains one or more prior messages from the same
  sender as the current authorized message
- **WHEN** the adapter prepares the authorized turn
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Third-party speaker sets third-party adopted policy state

- **GIVEN** the adopted window contains messages from `U222`
- **AND** the current authorized sender is `U111`
- **WHEN** the adapter prepares the authorized turn
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is true

### Requirement: Authorized sync watermark and gap computation

Threaded channel adapters SHALL maintain a durable per-thread authorized sync
watermark marking the highest thread ordering key whose authorized turn has
completed durably. Adapters MAY also persist a pending cursor for the highest
authorized message accepted for enqueue but not yet durably completed.

For a new authorized inbound with ordering key `Y`, the adapter SHALL hydrate
messages whose ordering key is strictly greater than the watermark and strictly
less than `Y`. The threaded adapter owns source-thread gap fetch and watermark
bookkeeping, while the session owns adopted-context persistence and execution
linkage. When the hydrated gap is non-empty, the adapter SHALL require
adopted-context persistence to succeed before it asks the session to enqueue the
authorized turn. After enqueue acceptance, the adapter SHALL persist or retain a
pending cursor for `Y`. The adapter SHALL advance the durable watermark to `Y`
only after `TurnCompleted` or other durable turn completion for that authorized
message. This sequencing SHALL remain fail-closed for crash recovery: a crash
after enqueue acceptance but before durable completion SHALL NOT promote the
durable watermark. If adopted-context persistence fails, the turn SHALL NOT be
enqueued and neither pending cursor nor durable watermark SHALL advance. If
enqueue is not accepted after persistence succeeds, neither pending cursor nor
durable watermark SHALL advance.

The idempotency basis for adopted-context persistence SHALL be the current
authorized message identity within the session or thread. If the same
authorized message is retried or replayed after a record has already been
persisted, the session SHALL reuse the existing adopted-context record for that
message rather than creating a duplicate.

If no messages exist strictly between the watermark and `Y`, the adapter SHALL
skip adopted-context persistence and adopted-context projection entirely and
enqueue the current authorized message as an ordinary authorized turn.

#### Scenario: First authorized turn adopts full prior gap

- **GIVEN** no watermark exists for a thread
- **AND** an authorized inbound message arrives mid-thread
- **WHEN** hydration runs
- **THEN** all eligible prior thread messages before the current authorized
  message are treated as unsynced and adopted

#### Scenario: Durable watermark advances after durable completion

- **GIVEN** the current watermark is `X`
- **AND** an authorized inbound with ordering key `Y > X` is processed
- **WHEN** that authorized turn later emits `TurnCompleted` with durable
  completion
- **THEN** the durable watermark advances to `Y`

#### Scenario: Pending cursor is recorded after enqueue acceptance

- **GIVEN** the current watermark is `X`
- **AND** an authorized inbound with ordering key `Y > X` is processed
- **WHEN** the resulting authorized turn is accepted for enqueue
- **THEN** the adapter records a pending cursor for `Y`
- **AND** the durable watermark remains `X` until durable completion occurs

#### Scenario: Same authorized message replay reuses adopted-context record

- **GIVEN** authorized inbound `Y` already has a persisted adopted-context
  record for the same session and message identity
- **AND** the watermark has not advanced past `Y`
- **WHEN** that same authorized message is retried or replayed
- **THEN** the existing adopted-context record is reused
- **AND** no duplicate adopted-context record is created

#### Scenario: Watermark does not advance without durable completion

- **GIVEN** the current watermark is `X`
- **AND** hydration for authorized inbound `Y` succeeds
- **WHEN** durable turn completion is never observed for `Y`
- **THEN** the durable watermark remains `X`

#### Scenario: Persistence failure blocks enqueue and watermark advance

- **GIVEN** the current watermark is `X`
- **AND** hydration for authorized inbound `Y` succeeds
- **WHEN** adopted-context persistence fails
- **THEN** the authorized turn is not enqueued
- **AND** neither pending cursor nor durable watermark advances

#### Scenario: Inbound at or before watermark is stale for adoption

- **GIVEN** the current authorized sync watermark is `X`
- **WHEN** a threaded inbound event arrives with ordering key `<= X`
- **THEN** the event is treated as stale for adoption-gap computation
- **AND** no new unsynced adopted window is created from messages at or before
  `X`

### Requirement: Adopted-message inclusion metadata

Each adopted message in the hydrated gap SHALL record message id, sender id,
timestamp, and authority-at-inclusion. Authority-at-inclusion SHALL be captured
at adoption time from the same live turn-creation authorization basis applied
to the inbound event and SHALL be persisted in the adopted-context record.

The adopted-context metadata for the turn SHALL also preserve the complete set of
sender ids present in the adopted window. That provenance SHALL remain inclusive
of any non-empty adopted window and SHALL NOT omit self-only adopted history
merely because no third-party sender is present.

#### Scenario: Unauthorized speaker captured as pending at inclusion time

- **GIVEN** `AllowedUserIds` contains `"U111"`
- **AND** adopted gap history contains a message from `"U999"`
- **WHEN** the adopted-context record is written
- **THEN** that included message records `authority-at-inclusion=pending`

#### Scenario: Authorized historical speaker captured as authorized at inclusion time

- **GIVEN** `AllowedUserIds` contains `"U111"`
- **AND** adopted gap history contains a message from `"U111"`
- **WHEN** the adopted-context record is written
- **THEN** that included message records `authority-at-inclusion=authorized`

#### Scenario: Self-only adopted provenance is preserved

- **GIVEN** the adopted window is non-empty
- **AND** every adopted message sender id matches the current authorized sender
- **WHEN** adopted-context metadata is materialized
- **THEN** the adopted-speaker provenance still includes that sender id
- **AND** the turn still reports adopted context as present
