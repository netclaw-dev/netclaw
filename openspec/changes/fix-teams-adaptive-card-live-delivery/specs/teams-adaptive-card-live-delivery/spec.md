## Purpose

Provide a Teams approval-card delivery path that the native transport can serialize and send without data disclosure.

## ADDED Requirements

### Requirement: Teams emits a transport-serializable card message

Teams SHALL send a pending or terminal approval card in a native message activity that the configured Teams transport can serialize.
The card-only activity SHALL omit a text field when the transport permits an attachment-only message.
The change SHALL preserve ordinary text reply and typing behavior.

#### Scenario: Pending approval uses a card-only activity

- **GIVEN** a session emits a pending tool approval with a supported Teams destination
- **WHEN** the Teams channel prepares the outbound message
- **THEN** the outbound activity contains one Adaptive Card attachment and no card-only text value
- **AND** the native Teams activity serializer accepts the activity

#### Scenario: Plain text reply keeps its transport shape

- **GIVEN** a session emits a non-card text reply
- **WHEN** the Teams channel prepares the outbound message
- **THEN** the outbound activity contains the reply text and no Adaptive Card attachment

### Requirement: Teams card values conform to the card schema

Teams SHALL use schema wire values that match the Adaptive Card and Teams host contract.
The channel SHALL preserve opaque callback names and values.
The channel SHALL not add an `Action.Submit` fallback.

#### Scenario: Approval options retain their callback contract

- **GIVEN** an approval card with session-supplied options
- **WHEN** the Teams channel prepares the card
- **THEN** each action keeps `Action.Execute`, `netclaw-approval`, and the supplied opaque callback data
- **AND** the full outbound activity serializer accepts the card values

### Requirement: Teams exposes safe delivery-stage diagnostics

Teams SHALL record a safe diagnostic for payload construction, activity serialization, and transport create, reply, or update failures.
The diagnostic SHALL identify only the operation stage and exception type.
The diagnostic SHALL not contain identifiers, message text, card data, secrets, response bodies, or remote error text.

#### Scenario: Card serialization failure is diagnosable without disclosure

- **GIVEN** the native Teams activity serializer rejects a card message
- **WHEN** the Teams channel handles the failure
- **THEN** the channel records the serialization stage and exception type
- **AND** the diagnostic contains no outbound content or destination data
