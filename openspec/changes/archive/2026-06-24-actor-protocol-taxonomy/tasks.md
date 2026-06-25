## 1. Pre-flight & taxonomy scaffolding

- [x] 1.1 Enumerate every `INetclawSerializableMessage` type (persisted/wire) and confirm each has a manifest entry in `NetclawProtobufSerializer.TypeToManifest`, a `FromBinary` arm, and a `NetclawProtoMapper` mapping; record the full list. Fail the task if any persisted type relies on a type-name-embedding fallback serializer. — DONE: 27 types, all in the manifest table; `WithStrictSerialization()` + single `INetclawSerializableMessage` binding rules out any type-name fallback.
- [x] 1.2 Enumerate the transient set (`INoSerializationVerificationNeeded`, never persisted) — these move with zero wire risk. — DONE: `SessionOutput` hierarchy, `ICommandReply` family, `LlmMessages` internals, `DeliverTrustedSessionTurn`, `ToolInteraction*Response`.
- [x] 1.3 Make `INetclawSerializableMessage` inheritable by event markers: confirm the single Akka serialization binding remains exactly `INetclawSerializableMessage` (no second interface bound). — DONE: binding is `boundTypes: new[] { typeof(INetclawSerializableMessage) }` in `WithNetclawSerialization`.
- [x] 1.4 Pre-refactor byte-fixture capture — folded into 7.1: manifest strings + `.proto` shapes are frozen, so round-tripping the relocated/restructured types proves backward-compat without separate fixtures. — DONE (folded).

## 2. SessionProtocol (session-first, riskiest)

- [x] 2.1 Create `public static partial class SessionProtocol` (in `Sessions/`, split by category file) with nested markers `ISessionCommand`/`ISessionEvent`/`ISessionQuery`/`ISessionResponse`, where `ISessionEvent : IWithSessionId, INetclawSerializableMessage` and exposes `DateTimeOffset Timestamp`. External contract only. — DONE (6 files). Added `ISessionBroadcast` for `TurnBroadcast`/`CompactionBroadcast`.
- [x] 2.2 Move session commands (`SendUserMessage`, `DeliverTrustedSessionTurn`, `ToolInteractionResponse`, `ToolInteractionTextResponse`) under `// ===== Commands =====`, tagged `ISessionCommand`. Preserve `SendUserMessage`'s intentional dual serialize/no-verify marking. — DONE.
- [x] 2.3 Move session events (all 10) under `// ===== Events =====`, tagged `ISessionEvent`; each exposes `Timestamp` as a computed `DateTimeOffset` getter over its existing `…AtMs` field (no proto/wire change). — DONE.
- [x] 2.4 Move responses: `ICommandReply` renamed to `ISessionResponse`; `CommandAck`/`CommandNack` implement it (44 refs / 18 files). — DONE.
- [x] 2.5 Move the `SessionOutput` hierarchy under `// ===== Outputs =====` (transient `INoSerializationVerificationNeeded`); not journaled / not in manifest table. NOTE: deliberately NOT unified under `ISessionResponse` (would broaden `case ISessionResponse` matches). — DONE.
- [x] 2.6 Internal self-messages left actor-private in `Sessions/LlmMessages.cs` / inline in `LlmSessionActor.cs`; NOT promoted into `SessionProtocol`. — DONE (untouched).
- [x] 2.7 Serializer/mapper updated: nested types resolve via one `using static` line each; **every manifest string constant byte-identical** (verified by diff). — DONE.
- [x] 2.8 `using static Netclaw.Actors.Sessions.SessionProtocol;` added to 109 consumer files across 9 assemblies; 1 collision qualified. — DONE.
- [x] 2.9 Build green (0 errors, full solution) + `SerializationRoundTrip` 37/37 pass (independently re-verified) + 1574 session/protocol/channel/reminder tests pass. — DONE.

## 3. Sweep remaining actor protocols

- [x] 3.1 `ReminderProtocol`: wrapped (partial); `IReminderCommand/Query/Response`. Value objects (`ReminderId`/`Delivery`/`Schedule`/`Definition`/`Payload`/`Info`, `HistoryRecord`, enums) kept OUTSIDE — the 4 manifest-table types stay put, so no serializer change. — DONE.
- [x] 3.2 `BackgroundJobProtocol`: wrapped; commands/queries/responses/notifications in, `BackgroundJobId`/`Definition`/`Status` enum out. All transient (none in manifest). — DONE.
- [x] 3.3 `SubAgentProtocol`: wrapped (`RunSubAgent` cmd, `SubAgentResult` resp); `SubAgentDefinition` out; inline `SubAgentActor` records left actor-private. — DONE.
- [x] 3.4 `ToolApprovalProtocol`: `internal static class` (types are cross-actor but assembly-internal); kept exact original interface set (no new serialization markers → strict-serialization behavior preserved). — DONE.
- [x] 3.5 `MemoryProtocol`: NOT created — `MemorySidecarContracts.cs` holds LLM-JSON deserialization targets + recall DTOs (`MemoryProposal`/`RecallPlanningRequest`/`RecallQueryPlan`/`MemoryAnchor`/`MemoryRelation`), never `Tell`/`Ask`'d (verified). Per "non-message types stay out," they stay as data contracts; a protocol shell would be empty. `MemoriesDistilledV2` left in place (zero wire change). — DONE (deviation accepted).
- [x] 3.6 `ModelCapabilityProtocol`: `GetModelCapabilities`→`IModelCapabilityQuery`, `ModelCapabilitiesResponse`→`IModelCapabilityResponse`. — DONE.

## 4. Grab-bag namespace cleanup

- [x] 4.1 Confirmed value objects (`SessionId`, `TurnId`, `SenderId`, …) and helpers/DTOs remain OUTSIDE the protocol classes (still top-level records). — DONE.
- [x] 4.2 Channel value types (`ChannelInput`/`MessageSource`/`TurnContext`) not wrapped — only `using static` additions where they consume session types. — DONE.

## 5. Routing verification

- [x] 5.1 Confirm `SessionMessageExtractor` still resolves entity ids from `IWithSessionId` for nested records; no extractor edits required. — DONE (no extractor change; routing tests green).

## 6. Cross-facet de-duplication

- [x] 6.1 Turn (`TurnBroadcast` vs `TurnRecorded`): `TurnBroadcast` was dead (no producer/consumer) and is now DELETED (type, manifest `tb-v1`, proto, mapper, tests). Wire-safe — pub/sub-only, never journaled.
- [x] 6.2 Compaction (`CompactionBroadcast` vs `SessionCompacted`): `CompactionBroadcast` likewise DELETED (manifest `cb-v1`). `ISessionBroadcast` marker removed (orphaned).
- [x] 6.3 Tool approval: candidate-type collapse DONE — removed the duplicate nested `ApprovalCandidateRecord {Verb,Directory}` and retyped the event's `Candidates` to the existing `Netclaw.Security.ApprovalCandidate`; dropped a redundant conversion at persist+apply. The fuller `ToolApprovalDetails` shared-payload merge was DROPPED (user decision): its only benefit was consolidation, and it would couple a persisted security/audit event to a render payload across ~40 approval-flow sites with no behavioral gain. Wire-clean (manifest/proto unchanged); approval + round-trip suites green.
- [x] 6.4 Title (`SessionTitleSet` vs `SessionTitleOutput`): NOT NEEDED — `SessionTitleOutput` is built from raw title strings/DTOs, not projected from the event; no real duplication to remove.

## 7. Tests & quality gates

- [x] 7.1 `SerializationRoundTripTests` round-trips every relocated/restructured persisted type (incl. the unified-`ApprovalCandidate` `ToolApprovalRequested`); manifest strings + proto shapes unchanged ⇒ relocation proves backward-compat. — DONE.
- [x] 7.2 Manifest-completeness loud-fail is covered by the existing `UnregisteredMessage` test (unregistered `INetclawSerializableMessage` throws). — DONE.
- [x] 7.3 `dotnet build Netclaw.slnx` 0 errors; full `dotnet test Netclaw.slnx` ~5,496 passed / 0 failed (13 integration tests skipped — need external infra). — DONE.
- [x] 7.4 `dotnet slopwatch analyze` — no NEW violations (3 pre-existing TUI-test `SW004` warnings in files this change never touched). — DONE.
- [x] 7.5 `Add-FileHeaders.ps1 -Verify` — all files have headers. — DONE.

## 8. Sync & archive

- [x] 8.1 `/opsx-verify` the implementation against the spec/design. — DONE: all 9 spec requirements evidenced in code; build + full suite green; no critical issues.
- [x] 8.2 `/opsx-sync` — created `openspec/specs/actor-message-protocol/spec.md` (9 requirements). — DONE.
- [x] 8.3 `/opsx-archive` the change. — DONE.
