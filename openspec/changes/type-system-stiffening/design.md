## Context

Trust context in Netclaw flows from an inbound channel adapter through the
session pipeline into every tool-access and memory-scoping decision. The data
path is:

```
ChannelInput (adapter)
  → MessageSourceFactory.Create
    → MessageSource (per-turn trust snapshot)
      → TrustContextDeriver.Derive → EffectiveTrustContext
        → ToolAccessPolicy / memory gates / background jobs / sub-agents
```

Today, `ChannelInput`'s four trust fields (`Audience`, `Boundary`, `Principal`,
`Provenance`) are nullable with no default. `MessageSourceFactory.Create`
materialises a value with `input.X ?? options.DefaultX`, where the
`SessionPipelineOptions.DefaultX` properties carry permissive sentinels
(`TrustAudience.Public`, `SourceProvenance.StrictDefault()`). `MessageSource`'s
own trust fields carry the same sentinels as property-init defaults. The
compiler therefore cannot distinguish an adapter that deliberately omits trust
context from one that simply forgot — and a forgotten field silently produces
the most permissive trust label. PR #993 was exactly this failure: a
Personal-audience Slack DM lost its audience and was gated as Public.

Three persisted record types (`BackgroundJobDefinition`, `ActiveJobInfo`,
`ReminderDefinition`) carry the same sentinel-default shape on disk, with an
*elevated* default (`TrustAudience.Personal`) — a forgotten field there is a
silent privilege escalation, not just a degradation.

Constraints:
- The constitution forbids silent fallbacks, especially on security paths.
- No on-disk or on-wire format change is permitted in this change. Legacy
  documents must remain loadable through an explicit, loud path.
- Actor message types crossing the wire are protobuf-mapped; their record
  *shape* cannot change, but the trust fields involved here are not
  wire-serialized as nullable in a way this change alters.

## Goals / Non-Goals

**Goals:**

- Make the four trust fields (`Audience`, `Boundary`, `Principal`,
  `Provenance`) impossible to omit at any actor boundary — enforced by the
  compiler, not by review.
- Delete the `SessionPipelineOptions.DefaultX` escape hatch and the
  `MessageSourceFactory` fallback arms so there is no code path that
  synthesizes trust context.
- Convert elevated-fallback escalation sites to explicit `throw`.
- Type `ToolExecutionContext.Audience` and `RunSubAgent.Audience` as parsed
  `TrustAudience`, moving parse failure to construction time.
- Make persisted trust fields `required` while keeping legacy JSON documents
  loadable through a loud, operator-visible path (no on-disk migration).

**Non-Goals:**

- The broad value-object adoption pass (wrapping `SenderId`, `TurnId`,
  `ToolCallId`, etc.) — tracked separately.
- The Pass 5/6 primary-constructor and `required`-keyword cleanups on
  non-security records — cosmetic, separate change.
- Any change to wire or on-disk serialization format.
- Changing the *values* of fail-closed conservative fallbacks in
  `TrustContextDeriver` (`UntrustedExternal`, `StrictDefault()` when source is
  genuinely absent) — those are correct.

## Decisions

### D1 — `ChannelInput` / `MessageSource`: `required` properties, not primary constructors

Both records have ~15 properties. A primary constructor with 15 positional
parameters is unreadable. Use `required` on the four trust fields and leave the
rest as property-init. `required` gives the same compile-time enforcement
(every object initializer must set the field) without the positional-argument
noise. *Alternative considered*: primary constructor — rejected on readability
for types this wide.

### D2 — `SourceProvenance`: 2-parameter primary constructor

`SourceProvenance` has two trust fields (`TransportAuthenticity`,
`PayloadTaint`) and two optional metadata fields (`SourceScope`, `SourceKind`).
Callsite inspection confirms every construction site sets both trust fields
explicitly and most set `SourceKind`; `SourceScope` is frequently omitted.
A 2-parameter primary constructor forces the trust fields and keeps the
metadata as optional `init`:

```csharp
public sealed record SourceProvenance(
    TransportAuthenticity TransportAuthenticity,
    PayloadTaint PayloadTaint) : IWireType
{
    public string? SourceScope { get; init; }
    public string? SourceKind { get; init; }
}
```

The `StrictDefault()` factory is removed; the one genuinely conservative
fallback (in `TrustContextDeriver` when `source` is null) constructs
`new SourceProvenance(TransportAuthenticity.Unverified, PayloadTaint.Public)`
explicitly so the conservatism is visible at the callsite.

### D3 — Delete `SessionPipelineOptions.DefaultX` rather than make it `required`

The four `Default*` properties exist only to feed the `MessageSourceFactory`
fallback arms. Making them `required` would preserve the escape hatch. Deleting
them forces each of the five `BuildOptions()` consumers (Slack, Discord,
SignalR, Webhook, Reminder binding actors) to stamp explicit trust context onto
the `ChannelInput` they construct. The per-adapter values that previously lived
in `DefaultX` move to the adapter as named local constants or computed values.

### D4 — Elevated-fallback sites become `throw`, not fail-closed defaults

`SessionToolExecutionPipeline` (background-job submission) and `SubAgentActor`
both default a missing audience to `TrustAudience.Personal` — an escalation.
After D1/D3 the only way `source` is null at these sites is a programming
error. They become `throw new InvalidOperationException(...)`. This is not a
fail-closed default (which would be `Public`); it is a loud assertion that the
invariant held by D1 was violated. `NullPromptInjectionDetector` substitution
becomes `throw` for the same reason — the real detector is a DI singleton, so
null means broken wiring.

### D5 — `ToolExecutionContext.Audience`: `string?` → `TrustAudience?`

`ToolExecutionContext` is a mutable `class` (not a record); tools mutate it.
Changing `Audience` from wire-string `string?` to `TrustAudience?` moves the
parse to the point where the context is built (`SessionToolExecutionPipeline`,
`SubAgentActor`), so an unparseable value fails there rather than silently
degrading to `Public` inside `ToolAccessPolicy`. `Boundary` stays `string?` —
it is a free-form partition label with no parse step. `SecurityPolicyDefaults.ParseAudienceOrPublic`
and `ResolveAudienceWithFallback` become dead code on the read path and are
deleted. `RunSubAgent.Audience` changes correspondingly.

### D6 — Persisted records: reject legacy documents at load, no backfill

`BackgroundJobDefinition`, `ActiveJobInfo`, `ReminderDefinition` make their
trust fields `required` — this is the type-system win: every in-process
construction is compiler-enforced.

`ActiveJobInfo` is protobuf-serialized; proto3 has no notion of an absent
field, and a legacy record deserializes its audience to enum `0`, which is
`TrustAudience.Public` (fail-closed). So `ActiveJobInfo` needs no special
handling — `required` is purely a compile-time change there.

`BackgroundJobDefinition` and `ReminderDefinition` are JSON
(`BackgroundJobDefinitionStore`, `ReminderDefinitionStore`). A legacy document
that omits the trust keys (or carries an explicit `null`) is **rejected** at
load — not coerced to a substitute audience. On the deserialization path each
store parses the document into a `JsonObject` and checks for the trust keys via
a shared helper (`LegacyTrustFieldGuard.MissingTrustFields`); if any are
absent, the store logs an **error** naming the file and the missing fields,
and excludes the document — `Get` returns null, `List` skips it. The reminder
store returns the rejection without deleting the file (it is operator-authored
data, distinct from corrupt JSON, so the operator can repair or remove it).

There is no backfill. A job or reminder with no persisted trust context cannot
be run safely: its trust tier is unknown, and these features are typically
disabled at the most-restrictive audience — so a `Public` substitute would
fabricate a nonsensical state (a feature that is gated off), and a `Personal`
substitute would silently escalate privilege. *Alternatives considered*:
(a) backfill `Public` — rejected, it produces a job/reminder in a
contradictory state (running at an audience where the feature is disabled);
(b) backfill `Personal` — rejected, an elevated default is precisely the
anti-pattern this change exists to remove. Rejecting the document is the only
choice that neither escalates nor fabricates. Pre-#994 a legacy reminder
already failed (it threw at execution for a missing audience); rejecting it at
load is the same outcome, surfaced earlier and without a per-fire crash.

### D7 — Sequencing as four independent PRs

PR-A (`ChannelInput`/`MessageSource`/`SourceProvenance`/`MessageSourceFactory`/
`SessionPipelineOptions` + adapters), PR-B (elevated-fallback throws), PR-C
(`ToolExecutionContext`/`RunSubAgent` typing), PR-D (persisted records:
`required` trust fields + legacy-document rejection). PR-A is a prerequisite
for PR-B (it establishes the non-null `source` invariant). PR-C and PR-D are
independent of A/B. Each is independently reviewable and compiler-verified.

## Risks / Trade-offs

- **Large mechanical diff across channel adapters** → The compiler drives the
  refactor: every missing `required` field is a build error pointing at the
  exact callsite. Fix per error, no guesswork. Tests adapt the same way.
- **Legacy persisted documents stop loading** → A pre-#994 job/reminder file
  with no trust fields is rejected at load and no longer runs. Mitigation: the
  rejection is logged at error level naming the file and the missing fields,
  and the file is preserved so the operator can repair (add the fields) or
  remove it. For reminders this matches the pre-#994 outcome (a missing
  audience already failed at execution); the failure simply moves earlier and
  loses the per-fire crash loop.
- **`throw` on a missing turn source could crash a session if the invariant is
  wrong** → The invariant (every tool execution and background-job submission
  has a turn source) is established by D1/D3 making `MessageSource` mandatory.
  If a path genuinely has no source, the `throw` surfaces it in testing rather
  than letting it escalate silently in production. Acceptable: loud failure in
  a test beats silent escalation in prod.
- **`ToolExecutionContext.Audience` retype touches every tool** → Blast radius
  is bounded to tools that read `context.Audience` (enumerated in the proposal
  impact section). Mechanical; compiler-verified.

## Migration Plan

1. PR-A → PR-B → PR-C → PR-D land in order; each is a normal `dev`-branch PR
   with green build + tests.
2. No deployment-time migration. On first daemon start after PR-D, any legacy
   persisted job/reminder document missing trust fields is rejected at load
   with an error log; the file is preserved. An operator who wants such a job
   or reminder back adds the `audience`/`boundary` fields or recreates it.
   Regression tests exercise the legacy-document rejection for both stores.
3. **Rollback**: each PR is independently revertable. PR-D's rejection is
   confined to the two stores' deserialization paths; reverting it restores
   the prior behavior. No on-disk data is rewritten or deleted by this change.

## Open Questions

- None.
