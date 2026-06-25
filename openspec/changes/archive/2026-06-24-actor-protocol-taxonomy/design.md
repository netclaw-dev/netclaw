## Context

Netclaw's actor messages live mostly under `src/Netclaw.Actors/Protocol/`, a namespace that
mixes true messages (`Commands.cs`, `Events.cs`, `Broadcasts.cs`, `SessionOutput.cs`,
`CommandResponses.cs`, `ModelCapabilityMessages.cs`) with value objects (`SessionId`, `TurnId`,
`SenderId`), helpers (`ChatMessageConverter`, `InboxWriter`, `SessionMediaStore`,
`ToolInteractionResponseParser`), and DTOs (`SessionOutputDto`). The session actor's protocol is
spread across seven-plus files plus private records inline in `LlmSessionActor.cs` (~4628) and
`SubAgentActor.cs` (~1249). Other actors (`ReminderProtocol`, `BackgroundJobProtocol`,
`SubAgentProtocol`) are already cohesive but are flat top-level records with no category markers and
no static-class container.

The gold-standard reference, `petabridge/llm-email-gateway`, gives each entity a
`static class {Entity}Protocol` nesting a marker hierarchy (`I{Entity}Command/Event/Query/Response`)
rooted on a Domain `IWith{Entity}Id`, with banner-separated sections and Protobuf surrogates for the
wire. Netclaw already has the plumbing equivalents — `IWithSessionId`, `SessionMessageExtractor`,
`NetclawProtobufSerializer` (stable manifest strings), `ICommandReply` — but not the taxonomy.

Persistence is PostgreSQL-backed Akka.Persistence. The serializer
(`NetclawProtobufSerializer : SerializerWithStringManifest`, id 150) is bound to the single
`INetclawSerializableMessage` interface, looks up a `typeof`-keyed `TypeToManifest` table to emit a
manifest, and `FromBinary`-dispatches on stable manifest strings (`"tr-v1"`, …) into
`NetclawProtoMapper`. This is the constraint that makes a type-reorganization safe.

## Goals / Non-Goals

**Goals:**

- One `static class {Entity}Protocol` per entity, nesting the four category markers + records,
  rooted on `IWith{Entity}Id`.
- Make `I{Entity}Event : INetclawSerializableMessage` so persisted events inherit the serializer tag.
- Unify Ask replies under `I{Entity}Response` (session `CommandAck`/`CommandNack` become
  `ISessionResponse`).
- Consolidate the fragmented session protocol; lift inline actor records into their protocol class.
- Reduce hand-duplicated fields across event/broadcast/output facets via shared payloads/projection.
- Document the taxonomy as a durable convention (`actor-message-protocol` capability).

**Non-Goals:**

- No change to wire format, manifest strings, `.proto` shapes, or persisted-event semantics.
- No behavioral change to session/subagent/job/scheduling/tool-approval logic — pure reorganization.
- No new serializer, no second serializer binding, no migration of existing journals.
- Channel value types (`ChannelInput`, `MessageSource`, `TurnContext`) are not reshaped into a protocol.

## Decisions

**D1 — Nested `static class {Entity}Protocol` (vs flat records + markers).**
Adopt the gateway's nested-static-class layout. Rationale: strongest scoping; `SessionProtocol.X`
reads as "session message X"; the markers are nested too (`SessionProtocol.ISessionCommand`).
Alternative considered: keep flat top-level records and only add marker interfaces — lower call-site
churn but weaker namespacing and doesn't match the reference. Chosen against because the user wants
the gold-standard shape and the churn is mechanically contained (see D5). For the session entity,
whose external surface exceeds the repo's ~600-line file norm, `{Entity}Protocol` is a
`public static partial class` split across category files (`SessionProtocol.Commands.cs`,
`.Events.cs`, `.Responses.cs`, `.Outputs.cs`, with markers in `SessionProtocol.cs`); smaller
protocols stay single-file like the gateway.

**D1a — External contract only; internal self-messages stay actor-private.**
`{Entity}Protocol` contains only what crosses the actor boundary: commands/queries received,
responses/outputs sent, events persisted. Internal self-messages (`LlmResponseReceived`,
`ProcessingWatchdogExpired`, `RoutedSkill*`, `ToolExecutionCompleted`, …) are the actor's private
state-machine plumbing — no routing id, never serialized — and remain private to the actor
implementation (their existing `Sessions/LlmMessages.cs` `internal` file and inline private records),
explicitly NOT promoted into the protocol class. Rationale (user direction): keep the public protocol
surface to "what can be sent to / received from the session"; promoting internals would bloat the
contract and blur the boundary. Alternative considered: a sibling `SessionInternalProtocol` class —
rejected as unnecessary ceremony; actor-private records already express "internal".

**D2 — `I{Entity}Event : INetclawSerializableMessage` (fold the serializer tag into the taxonomy).**
Events are the bulk of persisted types and are always journaled, so the Event marker is the natural
carrier of "must serialize." This removes the orthogonal, forgettable second marker for the common
case. Value objects (`SessionId`, `ReminderId`) and cross-wire commands (`SendUserMessage`) still
implement `INetclawSerializableMessage` explicitly. Alternative: bind the serializer to each
`I{Entity}Event` interface directly — rejected because `INetclawSerializableMessage.cs` documents a
hard rule that only one interface may be bound (Akka's `FindSerializerForType` short-circuits on first
match; multiple bindings are iteration-order-dependent). Inheritance gives the ergonomics without a
second binding.

**D3 — Responses are a first-class fourth category.**
`ICommandReply`/`CommandAck`/`CommandNack` become `ISessionResponse` members; reminders/jobs/subagents
gain their own `I{Entity}Response` families replacing ad-hoc inline reply records. Rationale: callers
can declare a typed Ask response per entity; consistency across actors.

**D4 — Session keeps a distinct Output facet.**
`SessionOutput` (16 subtypes) is the filtered pub/sub subscriber stream, not a journal record. It stays
a separate, transient facet. It is explicitly *not* merged into `ISessionEvent`, but cross-facet field
duplication with events is reduced (D6).

**D5 — `using static` for call-site migration.**
Each consumer file adds `using static Netclaw.Actors.Protocol.SessionProtocol;` (etc.) so existing
unqualified names (`new SendUserMessage(...)`, `case TurnRecorded`) keep compiling. `using static`
imports nested types, so the diff is mostly one import line per file plus collision fixes. Where two
imported protocols expose a same-named nested type, qualify at that site.

**D6 — Cross-facet de-duplication by shared payload/projection, conservatively.**
Where a fact is re-declared across facets, share a canonical payload or project one facet from another:

| Domain fact | Event | Broadcast | Output |
|---|---|---|---|
| Turn | `TurnRecorded` | `TurnBroadcast` | `TextOutput`+`TurnCompleted` |
| Compaction | `SessionCompacted` | `CompactionBroadcast` | `CompactionOutput` |
| Title | `SessionTitleSet` | — | `SessionTitleOutput` |
| Tool approval | `ToolApprovalRequested`/`Resolved` | — | `ToolInteractionRequest`/(`Response` cmd) |

`TurnBroadcast`/`CompactionBroadcast` carry a strict subset of their event — the concrete first win.
Do not merge forms that legitimately differ; this is a reduction pass, not a forced unification.

**D6 outcome (post-implementation).** The hypothesized event↔broadcast/output duplication mostly
did not exist in live code:
- `TurnBroadcast`/`CompactionBroadcast` turned out to be **dead** (full serialization scaffolding,
  zero producer/consumer) and were **removed** outright — a larger reduction than a projection
  factory would have given.
- `SessionTitleOutput` is built from raw strings, not projected from `SessionTitleSet` — no
  duplication to collapse.
- The realized de-dup is the **`ApprovalCandidate` type collapse** (the event's nested
  `ApprovalCandidateRecord` and `Netclaw.Security.ApprovalCandidate` were identical → unified).
- The fuller `ToolApprovalDetails` shared-payload merge was **dropped**: its only benefit was
  consolidation, and it would couple a persisted security/audit event to a render payload across
  ~40 approval-flow sites with no behavioral gain. The audit and render facets legitimately differ.

**D7 — Sequence session-first.**
Session is riskiest (most persisted events + the manifest table) and highest value. Migrate it first,
prove the round-trip, then apply the now-validated mechanical pattern to the smaller protocols.

## Risks / Trade-offs

- **[Persisted journal breaks if a moved type changes its wire identity]** → Manifest strings and
  `.proto` shapes are frozen; only `typeof(...)` references in `NetclawProtobufSerializer.TypeToManifest`
  and `NetclawProtoMapper` are repointed. `FromBinary` dispatches on the manifest string, which is
  unchanged. Guarded by extending `SerializationRoundTripTests` with pre-refactor byte fixtures.
- **[A type using a type-name-embedding fallback serializer is moved]** → Pre-flight: confirm every
  persisted type is in the protobuf serializer's table (manifest-based). Transient
  (`INoSerializationVerificationNeeded`) types never persist and are free to move.
- **[Wide call-site churn across the full sweep introduces compile breakage]** → `using static` keeps
  names unqualified; the compiler surfaces every missed site; session-first sequencing bounds blast
  radius per step.
- **[Name collisions when a file imports two protocols via `using static`]** → Qualify the colliding
  reference at that site; collisions are compile errors, not silent.
- **[Over-eager de-duplication couples a persisted form to a presentation form]** → D6 is conservative:
  reduce only equivalent fields; keep distinct shapes where the persisted vs filtered-wire forms differ.

## Migration Plan

1. **Pre-flight**: enumerate all `INetclawSerializableMessage` (persisted/wire) types; confirm each has a
   manifest + proto mapping and none rely on a type-name-embedding serializer. List the transient set.
2. **Markers**: add nested `I{Entity}Command/Event/Query/Response` per protocol; make
   `I{Entity}Event : INetclawSerializableMessage`.
3. **Session first**: wrap/consolidate `SessionProtocol`; move events, commands, responses, internal
   records, and the Output hierarchy in; repoint `typeof` refs; add `using static` at call sites;
   extend round-trip tests with byte fixtures; green build + tests.
4. **Sweep the rest**: `ReminderProtocol`, `BackgroundJobProtocol`, `SubAgentProtocol`,
   `ToolApprovalProtocol`, `MemoryProtocol`, `ModelCapabilityProtocol` — same mechanical pattern.
5. **De-dup pass** (D6): share/project the turn and compaction payloads.
6. **Gates**: `dotnet build` + `dotnet test`, `dotnet slopwatch analyze`, `Add-FileHeaders.ps1 -Verify`.

**Rollback**: the change is source reorganization with frozen wire forms, so reverting the commits
restores the prior type layout with no data migration. No journal rewrite occurs in either direction.

## Open Questions

- ~~Final home for `SessionProtocol`~~ — RESOLVED: `Sessions/SessionProtocol*.cs`, `public static
  partial class` split by category, external contract only; internals stay actor-private.
- Whether the optional "single-place registration" simplification (co-locating manifest constant +
  proto-mapper entry per type) is worth it, or whether it reduces the loud-fail clarity that the current
  three-point registration provides. Treat as a stretch goal; decide after the core sweep lands.
- Whether `ModelCapabilityProtocol`'s `Get…` messages become `IModelCapabilityQuery` with a paired
  `…Response`, or stay command-shaped; lean query/response for taxonomy consistency.
