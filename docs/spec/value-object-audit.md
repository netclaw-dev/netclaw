# Value-Object Audit (issue #994, Pass 7)

Source: issue #994 type-system-stiffening plan, Pass 7.
OpenSpec change: `value-object-adoption`.

## Purpose

Issue #994's trust-tier hardening (PR #1001) made security-relevant record
fields `required` and non-nullable. It did not change the *types* of the values
those fields carry: a trust boundary is still a free-form `string`, a tool-call
id is an unwrapped `string`, a sender id is a bare `string`. The compiler cannot
tell a `SenderId` from a `SessionId` from any other `string`, so a caller can
pass the wrong same-typed value with no signal.

This document inventories the raw-primitive identifier and trust-label fields on
Netclaw's actor protocol surface and records, for each, whether to wrap it in a
value object. It is the reference for the `value-object-adoption` change's
implementation slices.

## Categories

Each audited field is assigned one recommendation:

- **wrap-with-existing** — a value object already exists; the field unwraps it
  to a primitive at this boundary. Route the existing type through.
- **wrap-with-new** — no value object exists; a new one is recommended.
- **leave-as-string (free-form)** — free-form human text; a value object adds
  no correctness.
- **leave-as-string (config-bound)** — a wire/config discriminator whose value
  object would force a wire- or config-format change. Deliberately left
  primitive.
- **already-wrapped** — already a value object at this boundary.

## Headline counts

| Recommendation | Count |
|---|---|
| wrap-with-existing | 22 |
| wrap-with-new | 21 |
| leave-as-string (free-form text) | 19 |
| leave-as-string (config-bound wire format) | 6 |
| already-wrapped | 1 |

## Value-object design rules

All value objects — existing and new — follow these rules. They extend the
house pattern established by `ToolCallId`, `ToolName` (`Netclaw.Tools`), and
`SessionId` (`Netclaw.Actors.Protocol`).

1. **Shape.** A `readonly record struct` with a single `Value` property.
   `struct`, not `class`: these sit on per-turn protocol messages and an
   allocation per identifier is not acceptable.
2. **Validation.** A value object with a defined validity rule has a validating
   constructor that throws on null/empty/malformed input. `TrustBoundary`,
   `SenderId`, `AgentName`, `ModelId` validate; pure correlation ids with no
   meaningful invariant (`TurnId`) may keep the minimal positional form.
3. **No implicit conversions.** A value object exposes no implicit conversion to
   or from its primitive — an identifier that silently decays to `string`
   provides no safety. An `explicit` operator from the primitive is permitted
   (consistent with the existing house types) and routes through the validating
   constructor. Read access is `.Value`.
4. **Named factories for constants.** Known constants are named static factories
   (`TrustBoundary.Public`, `.Personal`, `.Team`, `.TrustedInstance`), never
   magic string literals at the callsite.
5. **Serializer mapping preserves the wire/disk format.** A value object that
   crosses the protobuf or JSON persistence boundary maps to its underlying
   primitive: the `.proto` field stays primitive and the containing type's
   `ToProto`/`FromProto` gains a `.Value` / `new(...)` hop; a JSON-persisted
   field gets a `JsonConverter<T>` writing the bare primitive. On-wire and
   on-disk bytes are byte-identical to the pre-value-object representation.
6. **Namespace.** Each value object lives in the namespace closest to its domain
   owner — `TrustBoundary` and `ModelId` in `Netclaw.Configuration`; `SenderId`,
   `TurnId`, `TurnNumber` in `Netclaw.Actors.Protocol`; `AgentName` in
   `Netclaw.Actors.SubAgents`.

The `default(struct)` hole — `default(TrustBoundary)` bypasses the constructor
and yields `Value == null` — is an accepted, documented limitation. Every
value-object-typed field is a `required` non-nullable member, so a `default`
instance can only arise from explicit `default(T)` or uninitialized storage,
never from normal record construction.

## Top-10 highest-impact opportunities

Ranked by occurrence × semantic weight.

1. **`TrustBoundary`** (wrap-with-new) — replaces `string Boundary` across 12+
   types (`MessageSource`, `ChannelInput`, `StartBackgroundJob`,
   `CancelBackgroundJob`, `QueryBackgroundJob`, `BackgroundJobDefinition`,
   `ActiveJobInfo`, `ReminderDefinition`, `RunSubAgent`, `ToolExecutionContext`,
   memory query args). A security-critical partition label, today a free-form
   string with magic constants in `SecurityPolicyDefaults`. Named factories
   `Public`/`Personal`/`Team`/`TrustedInstance`.
2. **`SenderId`** (wrap-with-new) — replaces `string SenderId` on
   `ToolInteractionResponse`, `ChannelInput`, `MessageSource`,
   `ConnectionIdentity`, `StartBackgroundJob`, `BackgroundJobDefinition`,
   `ChannelSecurityContext`, `SlackThreadInbound`, plus adopted-context records.
   The channel layer already has `SlackUserId` / `DiscordUserId`; the protocol
   layer unwraps them.
3. **`ToolCallId`** (wrap-with-existing) — unwrapped on
   `ToolInteractionResponse.CallId`, `ToolCallOutput.CallId`,
   `ToolResultOutput.CallId`, `ToolInteractionRequest.CallId`,
   `ToolAuditEntry.CallId`, `SerializableChatMessage.ToolCallId`,
   `SerializableToolCall.CallId`, `DiscordApprovalResponse.CallId`,
   `SlackApprovalResponse.CallId`. The approval-flow lynchpin.
4. **`ToolName`** (wrap-with-existing) — unwrapped on `ToolCallOutput.ToolName`,
   `ToolResultOutput.ToolName`, `ToolInteractionRequest.ToolName`,
   `ToolAuditEntry.ToolName`, `SerializableToolCall.Name`.
5. **`AgentName`** (wrap-with-new) — `SubAgentDefinition.Name`,
   `SubAgentResult.AgentName`, `SubAgentNotification.AgentName`,
   `SubAgentOutput.AgentName`, `CompletedSubAgentRun.AgentName`,
   `AcceptedSubAgentFinding.AgentName`.
6. **`ModelId`** (wrap-with-new) — `GetModelCapabilities.ModelId`,
   `ModelCapabilitiesResponse.ModelId`, `CapabilityResolved.ModelId`,
   `DiscoveredModel.ModelId`.
7. **`TurnNumber`** (wrap-with-new) — `TurnCompleted.TurnNumber`,
   `DeliveryFailed.TurnNumber`, `SessionSnapshot.EligibleDeliveryTurnNumber`,
   `SessionOutputDto.TurnNumber`. Used for stale-feedback rejection — a wrong
   `int` is silently dropped.
8. **`TrustAudience`** (wrap-with-existing enum) — `RunSubAgent.Audience` and
   `ToolExecutionContext.Audience` were `string?` wire values; issue #994
   Pass 3 already converted both to the parsed enum. No further work.
9. **`ChannelType`** (wrap-with-existing enum) — still unwrapped on
   `RunSubAgent.ChannelType`, `ToolExecutionContext.ChannelType`,
   `ReminderDelivery.Transport`.
10. **Unix-ms timestamp fields** — `long …AtMs` on 18+ protocol records. Not a
    single value object; most already carry a computed `DateTimeOffset`
    companion property. Recommendation: keep `long` for the protobuf/JSON wire
    and complete the existing companion-property pattern everywhere, rather
    than introduce a timestamp value object.

## Full inventory by recommendation

### wrap-with-existing (22)

- `ToolCallId` — the 9 `CallId` sites listed above.
- `ToolName` — the 5 sites listed above.
- `SessionId` — DTO and persisted-record fields that carry the session entity
  key as a raw `string`.
- `BackgroundJobId` — `ActiveJobInfo`, `BackgroundJobDefinition` (and the job
  command/query messages).
- `ReminderId` — `ReminderDefinition` and the reminder messages.
- `MimeType` — `FileOutput`, `SerializableMediaReference`, `FileAttachmentInfo`.
- `ChannelType` (enum) — `RunSubAgent.ChannelType`,
  `ToolExecutionContext.ChannelType`, `ReminderDelivery.Transport`.
- Memory enums (`MemoryRecallMode`, `MemoryProposalOperation`, `MemoryClass`,
  `SubjectKind`, `MemorySensitivity`) — downgraded to wire strings on
  `MemoryProposal`; see Pass 7e.

If `BackgroundJobId`, `ReminderId`, or `MimeType` is found not to exist as a
value object during implementation, treat that field as wrap-with-new instead.

### wrap-with-new (21)

- `TrustBoundary` — the 12+ `string Boundary` sites (top-10 #1).
- `SenderId` — the 9 `string SenderId` sites (top-10 #2).
- `AgentName` — the 6 sub-agent sites (top-10 #5).
- `ModelId` — the 4 model-capability sites (top-10 #6).
- `TurnNumber` — the 4 turn-ordinal sites (top-10 #7).
- `TurnId` — turn correlation id, where distinct from `TurnNumber`.
- `ApprovalOptionKey` — the approval-option discriminator.
- `ApprovalVerb` — the approval verb/action discriminator.
- `SkillName` — skill identity on skill messages.
- `WebhookEventType` — webhook event discriminator.
- `WebhookDeliveryId` — webhook delivery correlation id.
- `SourceScope` / `SourceKind` — the optional `SourceProvenance` metadata
  fields.
- `CheckpointTriggerType` — string on `ObservedMemoryCheckpointPayload`; an enum
  exists, see Pass 7e.

### leave-as-string

- **Free-form text (19)** — message bodies, rationales, error messages,
  human-authored descriptions, display labels. A value object adds no
  correctness; these stay `string`.
- **Config-bound wire format (6)** — wire/config discriminators (`McpServerEntry`,
  `ProviderEntry`, `ToolConfig`, `WebhookRouteConfig` field set) whose value
  object would force a wire- or config-format change. Deliberately left
  primitive; document the downgrade at the boundary.

### already-wrapped (1)

- One field already carries its value object correctly at the boundary; no
  action.

## Cross-cutting notes

- **Slack lags Discord.** `DiscordApprovalResponse` uses the `DiscordUserId`
  value object for `SenderId` / `RequesterSenderId`; `SlackApprovalResponse`
  uses raw `string` for the same fields. The `SenderId` work (Pass 7c) closes
  this gap.
- **Wire format is frozen.** Pass 7 changes only in-memory field *types*. Every
  protobuf-registered type (`SerializableChatMessage`, `SessionSnapshot`,
  `TurnRecorded`, …) and JSON-persisted type (`BackgroundJobDefinition`,
  `ReminderDefinition`) keeps byte-identical serialization via the
  serializer-mapping rule. Their record *shapes* (property-init form) are also
  left alone — Pass 5's primary-constructor migration explicitly excludes
  wire-serialized records.
