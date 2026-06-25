## Why

Netclaw's actor messaging has drifted from the protocol-design conventions used in our
gold-standard reference, `petabridge/llm-email-gateway`: the `Netclaw.Actors/Protocol/`
namespace is a grab-bag of messages, value objects, helpers, and DTOs; there is no
Command/Event/Query/Response taxonomy; the session actor's protocol is fragmented across
seven-plus files (and inline records inside `LlmSessionActor`/`SubAgentActor`); Ask-reply
patterns differ per actor; the same domain fact is hand-duplicated across persisted-event,
broadcast, and subscriber-output facets; and the serializer "tag" (`INetclawSerializableMessage`)
is an orthogonal marker that is easy to forget. This makes message handling and routing harder
to read, harder to extend safely, and easy to get wrong — and there is no documented convention
to hold the line going forward.

## What Changes

- Introduce a **per-entity message taxonomy** as a documented convention: each entity actor owns
  one `public static class {Entity}Protocol` (nested, gateway style) with a nested marker hierarchy
  rooted on its routing id (`IWith{Entity}Id`):
  - `I{Entity}Command` — imperative requests.
  - `I{Entity}Event` — past-tense **persisted** facts; carries `DateTimeOffset Timestamp`.
  - `I{Entity}Query` — read requests.
  - `I{Entity}Response` — Ask replies (subsumes today's `ICommandReply` family).
  - Session-only fifth facet: `SessionOutput` — the filtered pub/sub subscriber stream.
- **Fold serializer tagging into the taxonomy**: `I{Entity}Event : INetclawSerializableMessage`, so
  persisted events inherit the serializer binding and cannot be declared without it. Keep exactly
  one serializer-binding interface (per the existing single-binding rule).
- **Full sweep** wrapping every actor protocol as a static class with the marker hierarchy:
  `SessionProtocol`, `ReminderProtocol`, `BackgroundJobProtocol`, `SubAgentProtocol`,
  `ToolApprovalProtocol` (assembly-internal), `ModelCapabilityProtocol`. (The memory sidecar
  contracts in `MemorySidecarContracts.cs` are data DTOs, not actor messages — they are left
  as-is, not wrapped.)
- **Consolidate** the session protocol from `Commands.cs` / `Events.cs` / `CommandResponses.cs` /
  `Broadcasts.cs` / `SessionOutput.cs` / `Sessions/LlmMessages.cs` (and inline actor records) into
  `SessionProtocol`; separate non-message types (value objects, helpers, DTOs) out of the protocol
  classes.
- **Reduce duplication** where one domain fact is re-declared across event/broadcast/output facets
  (turn, compaction, title, tool-approval) by sharing a canonical payload or projecting the
  broadcast/output from the event — without merging forms that legitimately differ.
- **No BREAKING wire change**: manifest strings and Protobuf wire shapes are unchanged. Moving types
  into nested classes is journal-safe because `FromBinary` dispatches on stable manifest strings, not
  type names; only `typeof(...)` references in the serializer/mapper are updated.

## Capabilities

### New Capabilities
- `actor-message-protocol`: The actor message-protocol taxonomy and serialization-tagging convention —
  the four message categories (+ session Output facet), the nested `static class {Entity}Protocol`
  layout rooted on `IWith{Entity}Id`, the `Event ⇒ serializable` rule with a single serializer binding,
  the cross-facet de-duplication policy, and the wire-compatibility (manifest-stability) constraint
  that makes the reorganization safe.

### Modified Capabilities
<!-- None. This is a structural reorganization; it does not change the spec-level behavioral
     requirements of netclaw-session, netclaw-subagents, background-job-execution,
     netclaw-scheduling, netclaw-model-capabilities, or tool-approval-gates. Their message
     *organization* changes, not their behavior. -->

## Impact

- **Code**: `src/Netclaw.Actors/Protocol/*` (Commands, Events, Broadcasts, SessionOutput,
  CommandResponses, ModelCapabilityMessages), `src/Netclaw.Actors/Sessions/*` (LlmMessages,
  MemorySidecarContracts, inline records in LlmSessionActor), `Reminders/ReminderProtocol.cs`,
  `Jobs/BackgroundJobProtocol.cs`, `SubAgents/SubAgentProtocol.cs` (+ inline in SubAgentActor),
  `Tools/ToolApprovalMessages.cs`.
- **Serialization**: `Serialization/NetclawProtobufSerializer.cs` (`typeof` table),
  `Serialization/NetclawProtoMapper.cs` (type dispatch),
  `Serialization/INetclawSerializableMessage.cs` (Event marker inheritance). Manifest strings and
  `.proto` wire shapes are NOT changed.
- **Routing**: `Routing/SessionMessageExtractor.cs` matches on `IWithSessionId`, which nested records
  still implement — unaffected.
- **Call sites**: consumers across `Netclaw.Actors`, channel adapters, and HTTP callbacks adopt
  `using static` imports to minimize churn.
- **Tests**: extend `Netclaw.Actors.Tests/Protocol/SerializationRoundTripTests.cs` to prove every
  nested persisted type still round-trips (including pre-refactor byte fixtures).
- **Traceability**: no user-facing PRD; this is an internal architecture-quality change anchored to
  the constitution's Universal Quality Bar (transport-agnostic actor boundaries, framework-owned and
  serialization-safe persistence types, reuse-before-you-add) and the gold-standard reference
  `petabridge/llm-email-gateway`.

## Out of Scope

- No changes to wire format, manifest strings, or persisted-event semantics.
- `SendUserMessage`'s intentional dual `INetclawSerializableMessage` + `INoSerializationVerificationNeeded`
  marking (ephemeral `Source` dropped on persistence) is preserved, not "fixed".
- Channel value types (`ChannelInput`, `MessageSource`, `TurnContext`) remain shared cross-cutting
  types, not an actor protocol.
