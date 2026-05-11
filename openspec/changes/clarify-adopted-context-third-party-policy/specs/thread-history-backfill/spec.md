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
