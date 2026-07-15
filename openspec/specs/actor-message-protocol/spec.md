# actor-message-protocol Specification

## Purpose

Establish the convention for how actor messages are organized in Netclaw: each
entity actor owns one `public static class {Entity}Protocol` holding its
**external** message contract (commands and queries received, responses and
outputs sent, events persisted) under a nested Command/Event/Query/Response
marker hierarchy rooted on the entity's routing id (`IWith{Entity}Id`). Internal
self-messages stay actor-private; persisted events inherit the serializer tag via
`I{Entity}Event`; and reorganization is wire-safe because the protobuf serializer
dispatches on stable manifest strings rather than .NET type names — letting the
type system itself express which messages cross which boundary.

## Requirements

### Requirement: Per-entity external protocol container

Every actor entity SHALL declare its **external** messages — those that cross the actor boundary
(commands and queries received, responses and outputs sent, events persisted) — inside a single
`public static class {Entity}Protocol` type (`partial` across category files when size warrants).
Types that are not actor messages — value objects, helpers, mappers, codecs, parsers, and DTOs —
MUST NOT be nested inside the protocol class.

#### Scenario: Messages are namespaced under the protocol class

- **WHEN** a developer references a session command
- **THEN** it is reached as `SessionProtocol.SendUserMessage`, not as a bare top-level type in a shared `Protocol` namespace

#### Scenario: Non-message types stay out of the protocol class

- **WHEN** a value object such as `SessionId` or a helper such as `ChatMessageConverter` is needed
- **THEN** it is defined outside the `{Entity}Protocol` class and is NOT one of its nested types

### Requirement: External contract separated from internal plumbing

The `{Entity}Protocol` class SHALL contain only the entity's external contract. Internal
self-messages — messages an actor tells itself to drive its own state machine, which never cross
the actor boundary, carry no routing id, and are never serialized — MUST NOT appear in
`{Entity}Protocol`. They remain private to the actor's implementation (private nested records in
the actor file, or an actor-private message file). This keeps the public protocol surface limited
to what callers can send to or receive from the entity.

#### Scenario: Internal self-message is not part of the protocol

- **WHEN** the session actor tells itself `LlmResponseReceived` or `ProcessingWatchdogExpired`
- **THEN** that type is actor-private (not a member of `SessionProtocol`) and implements `INoSerializationVerificationNeeded`

#### Scenario: External message is part of the protocol

- **WHEN** a caller sends `SendUserMessage` or the session emits a `SessionOutput`
- **THEN** that type is a member of `SessionProtocol`, because it crosses the actor boundary

### Requirement: Four message categories with nested markers

Each `{Entity}Protocol` SHALL define a nested marker-interface hierarchy rooted on the entity's
routing identity interface `IWith{Entity}Id`:

- `I{Entity}Command : IWith{Entity}Id` — an imperative request to perform an action.
- `I{Entity}Event : IWith{Entity}Id` — a past-tense, persisted fact that MUST expose a
  `DateTimeOffset Timestamp` sourced from `TimeProvider`.
- `I{Entity}Query : IWith{Entity}Id` — a read request that expects a response.
- `I{Entity}Response` — an Ask reply carrying the entity id.

Every message record in `{Entity}Protocol` MUST implement exactly one of these category markers,
**except** the session `SessionOutput` subscriber-stream facet, which is governed by its own
requirement below. There SHALL be no global `ICommand`/`IEvent`/`IQuery` base shared across
entities.

#### Scenario: Each message declares its category

- **WHEN** a new session message record is added
- **THEN** it implements one of `ISessionCommand`, `ISessionEvent`, `ISessionQuery`, or `ISessionResponse`

#### Scenario: Events expose a timestamp

- **WHEN** a type implements `I{Entity}Event`
- **THEN** it exposes a `DateTimeOffset Timestamp` property populated from the injected `TimeProvider`, not `DateTime.UtcNow`

#### Scenario: No cross-entity command base

- **WHEN** the protocol markers are declared
- **THEN** `ISessionCommand` and `IReminderCommand` share no common `ICommand` ancestor; each is rooted only on its own `IWith{Entity}Id`

### Requirement: Persisted events imply the serializer binding

The marker `I{Entity}Event` SHALL inherit `INetclawSerializableMessage` so that every persisted
event is automatically bound to `NetclawProtobufSerializer`. There SHALL remain exactly one
interface bound to `NetclawProtobufSerializer`. Value objects and the few commands that cross a
persistence or remoting boundary MUST implement `INetclawSerializableMessage` explicitly.

#### Scenario: Declaring an event auto-binds serialization

- **WHEN** a developer declares a record implementing `ISessionEvent`
- **THEN** the record is bound to `NetclawProtobufSerializer` without a separate `INetclawSerializableMessage` declaration

#### Scenario: A transient command needs no serializer tag

- **WHEN** a command is dispatched only in-process (implements `INoSerializationVerificationNeeded` and not `INetclawSerializableMessage`)
- **THEN** it is not bound to the protobuf serializer and carries no manifest entry

#### Scenario: Single serializer binding is preserved

- **WHEN** the Akka serialization bindings are configured
- **THEN** only `INetclawSerializableMessage` is bound to `NetclawProtobufSerializer`, so `FindSerializerForType` resolution is deterministic

### Requirement: Session subscriber-output facet

The session entity SHALL retain a distinct fifth facet, `SessionOutput`, for the filtered
pub/sub subscriber stream. `SessionOutput` and its subtypes MUST be transient
(`INoSerializationVerificationNeeded`) and MUST NOT duplicate the persisted `ISessionEvent`
record set; an output is a presentation projection, not a journal entry.

#### Scenario: Outputs are not persisted

- **WHEN** a `SessionOutput` subtype is published to subscribers
- **THEN** it is not written to the journal and is not registered in the serializer manifest table

### Requirement: Consistent Ask-reply responses

Actors that reply to an Ask SHALL return a type implementing their entity's `I{Entity}Response`
family rather than an ad-hoc inline record. The session acknowledgement/negative-acknowledgement
pair (`CommandAck`/`CommandNack`) MUST be expressed as `ISessionResponse` members.

#### Scenario: Reminder Ask returns a typed response

- **WHEN** a caller issues an Ask against the reminder actor
- **THEN** the reply implements `IReminderResponse`, mirroring the session `ISessionResponse` convention

### Requirement: Cross-facet message de-duplication

The system SHALL NOT hand-duplicate the field set of a domain fact across more than one facet.
When the same fact is represented as both a persisted event and a pub/sub broadcast or subscriber
output, those facets MUST share a single canonical payload record or derive one facet from another
via an explicit projection. Facets that legitimately differ — a persisted form versus a filtered
wire form — MAY retain distinct shapes, but equivalent fields MUST be reduced rather than repeated.

#### Scenario: Facets reference one canonical type

- **WHEN** the persisted approval event and the approval render output both express their candidate set
- **THEN** they reference a single shared `ApprovalCandidate` type rather than each defining its own identical record

### Requirement: Wire compatibility under reorganization

Reorganizing message types into nested `{Entity}Protocol` classes SHALL NOT change any persisted
wire form. Protobuf manifest strings and `.proto` message shapes MUST remain byte-stable;
only the `typeof(...)` references in `NetclawProtobufSerializer` and `NetclawProtoMapper` may be
updated to point at the relocated types. `FromBinary` MUST continue to dispatch on the stable
manifest string, never on a .NET type name.

#### Scenario: Pre-refactor journal bytes still deserialize

- **WHEN** a journal entry written before the reorganization is read back after types are moved into a nested protocol class
- **THEN** it deserializes successfully via its unchanged manifest string into the relocated nested type

#### Scenario: Manifest strings are unchanged

- **WHEN** the serializer's manifest table is reviewed after the change
- **THEN** every manifest constant (e.g. `"tr-v1"`) is identical to its pre-change value, with only the keyed `typeof(...)` reference updated

### Requirement: Routing by entity identity is preserved

Message routing SHALL continue to resolve the target entity from the `IWith{Entity}Id` marker.
Nesting a message inside `{Entity}Protocol` MUST NOT change which message extractor matches it,
because the nested record still implements the routing marker.

#### Scenario: Session extractor matches nested command

- **WHEN** `SessionMessageExtractor.EntityId` is given a `SessionProtocol.SendUserMessage`
- **THEN** it extracts the session id via the `IWithSessionId` marker exactly as before the nesting

### Requirement: Execution-scope refactoring preserves external actor contracts

Run scopes, child scopes, activity trackers, and working-context deltas introduced for tool execution SHALL be framework-owned local actor messages. The refactoring SHALL NOT change existing persisted event shapes or MCP protocol payloads. Local messages SHALL remain serialization-safe where they cross actor boundaries.

#### Scenario: Existing MCP caller invokes a tool

- **GIVEN** an MCP client using the tool schema from before this change
- **WHEN** it invokes the tool after the internal execution refactoring
- **THEN** the request and response protocol remain compatible
- **AND** internal run-scope types are not exposed in the MCP schema

#### Scenario: Actor recovers persisted session state

- **GIVEN** session events persisted before this change
- **WHEN** the updated session actor recovers them
- **THEN** recovery succeeds without a data migration
- **AND** volatile run scopes and Git snapshots are reconstructed only for new work
