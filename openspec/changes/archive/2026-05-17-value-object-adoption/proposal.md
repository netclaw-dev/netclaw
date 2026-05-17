## Why

Issue #994's trust-tier hardening (PR #1001) made security-relevant fields
`required` and non-nullable, but the *values* those fields carry are still raw
primitives — a trust boundary is a free-form `string`, a tool-call id is an
unwrapped `string`, a sender id is a bare `string`. The compiler cannot tell a
`SenderId` from a `SessionId` from any other `string`, so a forgetful caller can
pass the wrong same-typed value with no signal. The original issue #994 plan
scoped this value-object adoption pass out as "tracked separately"; this change
completes it, together with the two mechanical record-shape passes (primary
constructors, `required` keyword) from the same plan that never landed.

## What Changes

- **Pass 5 — primary-constructor migration.** Migrate ~22 non-security protocol
  records that have ≤3 required properties (and whose callsites already set all
  of them) to positional primary-constructor form. Purely mechanical; no
  behavior change. Wire-serialized record shapes are excluded.
- **Pass 6 — `required`-keyword pass.** Apply the `required` keyword to the few
  remaining records with 4+ logically-required properties still using bare
  `init` (notably `ToolAuditEntry`). No structural change.
- **Pass 7 — value-object adoption.**
  - **7a** Write `docs/spec/value-object-audit.md` — the full inventory of ~70
    raw-primitive protocol fields with per-field recommendations.
  - **7b** Wrap-with-existing: route ~22 fields that already have a value-object
    type (`ToolCallId`, `ToolName`, `SessionId`, `BackgroundJobId`,
    `ReminderId`, `MimeType`, `TrustAudience`, `ChannelType`, memory enums)
    through that type at every protocol boundary instead of unwrapping to the
    primitive.
  - **7c** Introduce the three highest-impact new value objects: `TrustBoundary`
    (replaces free-form `string Boundary` across 12+ types), `SenderId`,
    `AgentName`.
  - **7d** Introduce the remaining new value objects: `ModelId`, `TurnNumber`,
    `TurnId`, `ApprovalOptionKey`, `SkillName`, `WebhookEventType`,
    `WebhookDeliveryId`, `SourceScope`, `SourceKind`, `ApprovalVerb`.
  - **7e** Tighten memory and sub-agent finding messages to carry their existing
    enums (`MemoryClass`, `MemorySensitivity`, `CheckpointTriggerType`, etc.)
    instead of re-downgrading them to wire strings.
- **BREAKING** (internal API only) — protocol record field types change from
  primitives to value objects; every in-process construction site must supply
  the value-object type. There is **no** wire-format or on-disk-format change:
  each value object that crosses the protobuf/JSON boundary maps to its
  underlying primitive in serializer mapping, so on-wire and on-disk bytes are
  byte-identical.

## Capabilities

### New Capabilities

- `value-object-integrity`: Identifier and trust-label fields that cross actor
  boundaries SHALL be represented as validating value-object types rather than
  raw primitives. Value objects validate their input at construction, expose no
  implicit conversion to or from the primitive, provide named factories for
  known constants, and preserve the underlying wire/disk format through
  serializer mapping.

### Modified Capabilities

- (none) — Passes 5 and 6 are mechanical record-shape refactors with no
  requirement-level change. Pass 7's value-object conversions refine the field
  *types* of records owned by `trust-context-integrity`,
  `netclaw-input-adapters`, `background-job-execution`,
  `reminder-execution-history`, `netclaw-tools`, and `netclaw-subagents`, but do
  not change the requirement-level behavior those capabilities specify; the new
  `value-object-integrity` capability carries the cross-cutting invariant.

## Impact

- **Affected code**: `Netclaw.Actors` (`Protocol/`, `Channels/`, `Jobs/`,
  `Reminders/`, `SubAgents/`, `Sessions/`, `Memory/`, `Tools/`),
  `Netclaw.Tools.Abstractions` (`ToolExecutionContext`), `Netclaw.Configuration`
  (new `TrustBoundary`, `ModelId`), `Netclaw.Channels.Slack` /
  `Netclaw.Channels.Discord` ingress records, `Netclaw.Daemon` (webhooks).
- **New files**: value-object types placed in their domain-owning namespaces;
  audit document `docs/spec/value-object-audit.md`.
- **Serialization**: `NetclawProtobufSerializer` registrations and the JSON
  converters for persisted types (`BackgroundJobDefinition`,
  `ReminderDefinition`) gain value-object↔primitive mapping. The on-wire and
  on-disk bytes are unchanged — value objects are an in-memory correctness gate
  only. Any field whose value-object conversion would force a wire-format change
  is deliberately left as a primitive (the "leave-as-string (config-bound)"
  audit entries) and documented at the boundary.
- **APIs**: internal-only; no public NuGet surface change.
- **Tests**: `Netclaw.Actors.Tests`, channel and daemon test projects adapt
  mechanically to the value-object and primary-constructor shapes; serializer
  round-trip tests gain value-object coverage.
- **Security**: value objects narrow the bug class where a security-relevant
  value (trust boundary, sender id, audience) is silently swapped for a
  wrong-but-same-typed primitive. No runtime trust decision is changed.
- **In scope (MVP)**: Passes 5, 6, and 7a–7e as enumerated above.
- **Out of scope**: any wire-format / on-disk-format change; the 25
  "leave-as-string" audit entries (free-form text and config-bound wire
  discriminators); source-generated and protobuf-generated types; primary-ctor
  migration of wire-serialized record shapes.
- **Source**: issue #994 type-system-stiffening plan, Passes 5–7. No product
  PRD applies — this is an internal type-safety and security-hardening
  initiative, not a product feature.
