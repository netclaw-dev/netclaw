## Context

PR #1001 (issue #994, Passes 1–4) made trust-bearing record fields `required`
and non-nullable. The remaining issue #994 plan — Passes 5–7 — was left for a
follow-up: ~22 protocol records still suitable for primary-constructor form,
a handful still using bare `init` where `required` belongs, and ~43 raw-primitive
identifier / trust-label fields that a value object would make
compiler-checkable.

The repo already has three value objects — `ToolCallId`, `ToolName`
(`Netclaw.Tools.Abstractions`), `SessionId` (`Netclaw.Actors.Protocol`). They
establish a house pattern:

```csharp
public readonly record struct ToolCallId(string Value)
{
    public static explicit operator ToolCallId(string value) => new(value);
    public override string ToString() => Value;
}
```

A `readonly record struct`, a single positional `Value`, an **explicit**
(never implicit) operator from the primitive, and `ToString() => Value`.
`SessionId` additionally implements `INetclawSerializableMessage` and has a
manifest entry + `ToProto`/`FromProto` mapping in `NetclawProtoMapper`. None of
the three validate their input.

The full per-field audit lives in the planning doc and is published as
`docs/spec/value-object-audit.md` (task 7a).

## Goals / Non-Goals

**Goals:**

- Make identifier and trust-label confusion a compile error: a `SenderId` may
  not be passed where a `SessionId` is expected.
- Replace the free-form `string Boundary` — a security partition label with
  magic constants scattered in `SecurityPolicyDefaults` — with a `TrustBoundary`
  value object carrying named factories.
- Land Passes 5 and 6 (mechanical record-shape stiffening) that the issue #994
  plan specified but never shipped.
- Keep every on-wire (protobuf) and on-disk (JSON) byte identical.

**Non-Goals:**

- No wire-format or on-disk-format change. Anything that would force one is
  deferred.
- No conversion of free-form text or config-bound wire discriminators (the 25
  "leave-as-string" audit entries).
- No retrofit of the three existing value objects beyond what a touched
  callsite requires.
- No new runtime trust decision — value objects are a compile-time gate only.
- No primary-constructor migration of wire-serialized record *shapes* (their
  property-init form is load-bearing for the proto mappers).

## Decisions

### Decision: value-object shape extends the existing house pattern

New value objects follow `ToolCallId`/`SessionId`: `readonly record struct`,
single `Value`, `explicit` operator from the primitive, `ToString() => Value`.
`struct` (not `class`) because these sit on per-turn protocol messages and an
allocation per identifier is not acceptable.

Two extensions over the minimal existing form:

- **Validating constructor where a validity rule exists.** `TrustBoundary`,
  `SenderId`, `AgentName`, `ModelId` reject null/empty. `TrustBoundary` also
  trims and lower-cases to the canonical form `SecurityPolicyDefaults` expects.
  This requires the non-positional struct form (explicit `Value` property +
  hand-written constructor) so the validation has a hook; the `explicit`
  operator routes through that constructor. Pure correlation ids with no
  meaningful invariant (`TurnId`) may keep the minimal positional form.
- **Named static factories for known constants.** `TrustBoundary.Public`,
  `.Personal`, `.Team`, `.TrustedInstance` replace the
  `SecurityPolicyDefaults.PersonalBoundary` magic strings at their callsites.

*Alternative considered — `record class`*: rejected for allocation pressure on
hot protocol paths and nullable-reference ambiguity at every field.

*Alternative considered — closed enum for `TrustBoundary`*: rejected. A trust
boundary can be an instance- or deployment-scoped custom label; only the four
well-known values get factories. `TrustBoundary` stays an open string-backed
value object, not an enum.

### Decision: no implicit conversions; explicit operator retained

The constitution forbids **implicit** conversions on value objects — an
identifier that silently decays to `string` provides no safety. It does not
forbid an `explicit` operator, and the three existing value objects all have
one. New value objects keep the `explicit` operator for ergonomic boundary code
(channel adapters, serializer mappers) and expose `.Value` for read access.
No implicit operator is ever added.

### Decision: the `default(struct)` hole is a documented limitation

`default(TrustBoundary)` bypasses the constructor and yields `Value == null`.
Because every value-object-typed field is a `required` non-nullable member,
a `default` instance can only arise from explicit `default(T)` or uninitialized
storage — never from normal record construction. This matches the existing
value objects and is accepted as a known limitation rather than guarded at
every read.

### Decision: serializer mapping keeps wire and disk bytes identical

Value objects are an in-memory gate; the serialized representation does not
change.

- **Protobuf** (`NetclawProtobufSerializer` / `NetclawProtoMapper`): for a value
  object used as a *field* of a proto-mapped type, the `.proto` message keeps a
  primitive field; the containing type's `ToProto`/`FromProto` gains a `.Value`
  / `new(...)` hop — exactly what `SessionId` already does
  (`NetclawProtoMapper.cs`). No `.proto` schema edit. A value object that is
  itself a top-level `INetclawSerializableMessage` gets its own manifest entry
  and mapping, again over a primitive proto field.
- **JSON** (persisted `BackgroundJobDefinition`, `ReminderDefinition`): each
  value-object-typed field gets a `JsonConverter<T>` that reads/writes the bare
  primitive. Converters are registered on the persistence
  `JsonSerializerOptions`.
- A round-trip byte-equality test accompanies every serializer-registered or
  JSON-persisted type whose fields are wrapped.
- Where wrapping a field would force the wire/disk bytes to change, the field
  is left as the primitive and the deliberate downgrade is noted at the
  boundary (the "leave-as-string (config-bound)" audit entries).

### Decision: slice ordering — audit, mechanical passes, then value objects

1. **7a** — audit doc only, no code.
2. **Pass 5** — primary-constructor migration (~22 records). Independent,
   mechanical.
3. **Pass 6** — `required`-keyword pass (~1–5 records). Independent, mechanical.
4. **7b** — wrap-with-existing (22 fields). Lowest risk; the value object
   already exists and validates.
5. **7c** — `TrustBoundary`, `SenderId`, `AgentName`. Highest blast radius;
   `TrustBoundary` subsumes the `string Boundary` work from PR-A.
6. **7d** — remaining new value objects.
7. **7e** — memory / sub-agent finding enum unwrap fixes.

Each slice is one reviewable PR. Passes 5/6 and 7a carry no risk and can land
in any order relative to each other; 7b–7e are sequenced because later slices
build on the namespaces and serializer plumbing earlier ones add.

## Risks / Trade-offs

- **A `required`/value-object conversion surfaces a hidden authorization
  regression.** This already happened once in issue #994 — making
  `POST /api/reminders`' field required exposed a null authorization that had
  been papered over. → Mitigation: run a behavioral review (not just a compile)
  after each slice; pay special attention to any callsite that was relying on
  an empty/null primitive.
- **Serializer mapping drift changes wire or disk bytes.** A missed `ToProto`
  hop or an absent `JsonConverter` would silently alter persisted data. →
  Mitigation: a byte-equality round-trip test for every wrapped
  serializer-registered or JSON-persisted type; the `INetclawSerializableMessage`
  marker already fails loudly on a missing manifest/mapper entry.
- **Large mechanical blast radius causes review fatigue.** Hundreds of callsites
  change type. → Mitigation: compiler-driven (the build is the worklist);
  one slice = one PR; 7d is splittable if a single PR grows too large.
- **Value objects on protobuf-mapped types could tempt a record-shape change.**
  → Mitigation: Pass 5 explicitly excludes wire-serialized record shapes; Pass 7
  changes only field *types*, never the property-init shape of a proto-mapped
  record.

## Migration Plan

No data migration. Because protobuf and JSON bytes are unchanged, an upgrade is
a binary swap and a rollback is a binary rollback — a daemon running the new
binary reads documents and journal entries written by the old one and vice
versa. The round-trip byte-equality tests are the gate that keeps this true; if
one fails, the offending slice does not ship.

Each slice merges independently behind the compiler. There is no feature flag —
a value object is either adopted on a field or not, and the build enforces
consistency.

## Open Questions

- **`TurnNumber` vs `TurnId`** — the audit lists both. `TurnNumber` is the
  ordinal used for stale-feedback rejection; `TurnId` is a unique correlation
  id. They are kept distinct; confirm during 7d that both have real callsites
  before creating the second type.
- **Retrofitting the three existing value objects with validating
  constructors** — out of scope here; revisit only if a touched callsite
  depends on it.
- **`SenderId` vs the channel-layer `SlackUserId` / `DiscordUserId`** — the
  protocol-layer `SenderId` is the cross-channel identity; the channel layer
  keeps its channel-specific types and converts at ingress. Confirm no callsite
  needs a lossless round-trip between them.
