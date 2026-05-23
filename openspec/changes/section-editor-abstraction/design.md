## Context

The `netclaw init` wizard composed of `WizardOrchestrator` + a fixed list of
`IWizardStepViewModel`s produces a runnable Netclaw configuration but treats
the on-disk state as a write-once target. There is no shared abstraction for
"the editable surface of one configuration section," so every section's input
collection, validation, and persistence logic lives inline in its step
viewmodel. Three foundations from PR #432 partially anticipate the shared
abstraction:

- `WizardContext.ExistingConfig` is declared on the context object but
  never populated.
- `ConfigFileHelper` and `ProviderCredentialWriter` already implement the
  load-merge-write pattern, used today by `netclaw provider`/`model`/`mcp`
  CLI subcommands.
- Each `IWizardStepViewModel.OnEnter(context, direction)` already receives a
  direction marker, but no step uses it.

This change formalizes the shared abstraction so the next change can compose
existing step viewmodels into the new `netclaw config` command without
forking their logic. It also closes the long-standing reentrancy gap (#455):
re-running `netclaw init` over an existing install now produces a sensible
pre-filled wizard with merge-on-save semantics, rather than the prior
undefined behavior.

## Goals / Non-Goals

**Goals:**

- Define `ISectionEditor` such that any step viewmodel implementing it can
  be hosted either by the linear init wizard or by a single-step
  orchestrator that the next change introduces, with no per-host behavior
  difference visible to the user.
- Lock in three operational contracts that future section editors must
  honor: reentrancy (pre-fill from `ExistingConfig`), secret handling
  (never rehydrate; "leave blank to keep"), and merge-on-save
  (byte-equality of every other top-level section).
- Establish the audit + test harness up-front so the contracts are enforced
  from the first registered editor, not retrofitted later when drift has
  already begun.
- Refactor Provider, Identity, and Posture step viewmodels to implement
  `ISectionEditor`. Behavior inside today's linear init wizard remains
  observable-equivalent for first-run.
- Close #455 (reentrant init) as a byproduct of populating `ExistingConfig`
  at entry and switching `WizardConfigBuilder` to merge-on-save.

**Non-Goals:**

- Introducing the `netclaw config` command (next change).
- Adding the remaining seven section editors (next change).
- Simplifying the init wizard's step list to provider + identity +
  posture only (third change).
- Hot-reload of the running daemon on config change (out of scope; remains
  a documented manual-restart limitation).
- Section editor UI for sections that today are file-edited only
  (`Persistence`, `Logging`, `Telemetry`, etc.) — these stay on the
  exemption list.
- Reworking `netclaw provider`/`model`/`mcp` CLI subcommands to share
  backing logic with the new abstraction. Their existing behavior is
  unchanged; future work may unify them.

## Decisions

### D1. `ISectionEditor` as a viewmodel factory, not a viewmodel base class

The interface returns an `IWizardStepViewModel` from `CreateEditor(context)`
rather than extending the existing viewmodel base. This keeps the
orchestrator's lifecycle contract authoritative and avoids multiple
inheritance / diamond issues for step viewmodels that already extend a
shared base. It also lets a single `ISectionEditor` produce different
viewmodels for different contexts in the future (e.g. a future
"compact" view) without changing the interface.

Alternative considered: make `ISectionEditor` itself extend
`IWizardStepViewModel`. Rejected because it conflates "this thing is a
runnable step" with "this thing describes an editable section in the
registry"; the dashboard and audit code want the metadata without
constructing a runnable step.

### D2. Merge-on-save via existing `ConfigFileHelper` primitives

`WizardConfigBuilder` is refactored to call `ConfigFileHelper.LoadConfigFiles`
and `GetOrCreateSection` rather than building a fresh dictionary. The
existing primitives have already been proven by `ProviderCredentialWriter`
and the CLI subcommands; no new merge code is introduced. Each editor
contributes via an explicit `SectionContribution` record carrying
`Dictionary<string, FieldAction>` for non-secrets and
`Dictionary<string, SecretAction>` for secrets. The merge writer applies
the actions deterministically; "blank means X" is the editor's job to
interpret, not the merge layer's.

Alternative considered: introduce a fresh JSON-patch-style operation log.
Rejected because the existing dictionary-based pattern is already in
production use and a parallel mechanism would introduce a forking point.

### D3. Secret-presence lookup as a first-class API

`ConfigFileHelper.SecretPresent(paths, sectionId, key)` is added to satisfy
the "configured / not set" hint without exposing the decrypted value. This
keeps the secret-handling contract enforceable at the type level: editors
that need to show the hint cannot accidentally hold the decrypted value
because the API does not return one.

Alternative considered: have editors call the secrets protector and discard
the decrypted value after a length check. Rejected because the decrypted
value would still transit through process memory; a presence-only API
guarantees the value is never decrypted at all.

### D4. Audit walks the menu registry, not the full schema

`MenuRegistryAuditTests` walks `SectionEditorRegistry.All()`. Schema
sections without a registered editor are not audited unless they appear
in the exemption list. The audit's purpose is to enforce contracts on
editors we ship, not to demand editors for every schema knob; the
exemption list is the explicit "we know about this section and choose
not to expose it" record.

Alternative considered: walk the schema and require every top-level
section to either have an editor or an exemption. Rejected per planning
discussion: forcing editors for every schema knob produces shallow,
unhelpful UIs for sections nobody edits via TUI. The menu-driven audit
prevents drift on the surfaces we promise to users, which is the failure
mode that actually matters.

### D5. Refactor exactly three editors in this change

Provider, Identity, and Posture are the three steps that survive in the
simplified init wizard (third change). Refactoring them here lets us
verify the abstraction end-to-end against real editors without entangling
this change with the larger config-command surface. The remaining seven
editors are introduced as new `ISectionEditor` implementations in the
next change, alongside the dashboard that hosts them.

Alternative considered: refactor all ten existing init steps at once.
Rejected because it bloats this PR and ties the abstraction's correctness
to behavioral equivalence across far more surface area than necessary to
prove the contract.

### D6. `ExistingConfig` is `Dictionary<string, object>`, not strongly typed

Reuses the type already declared on `WizardContext`. Strongly-typed access
would require introducing a parallel typed view of `netclaw.json`, which
defeats the schema-as-source-of-truth principle. The dictionary form is
also forgiving across schema versions: an unknown key simply doesn't
surface in any editor's slice.

Alternative considered: bind to typed `*Config` records via
`IConfiguration`. Rejected because the merge step would then need to
re-emit the typed records as JSON, multiplying the round-trip surface
area and introducing per-property null/default ambiguity.

### D7. `WizardOrchestrator` gets a single-step constructor, not a new class

Existing orchestration logic (back/forward, dirty tracking, save flow)
already covers the single-step case; we add a constructor and a mode
flag rather than a parallel orchestrator type. This keeps the
orchestrator the single authority on step lifecycle.

Alternative considered: introduce `SectionEditorRunner` as a separate
host. Rejected because behavior would inevitably drift between two
orchestrators over time.

## Risks / Trade-offs

- [Refactor risk] Touching three existing step viewmodels could regress
  first-run init behavior. → Mitigation: existing `init-wizard.tape`
  smoke test continues to gate every PR. Round-trip xUnit tests added in
  this change provide finer-grained protection than the tape alone.

- [Merge-on-save regressions] If the merge logic loses precision on edge
  shapes (`JsonElement` value kinds, nested arrays), unrelated sections
  could silently change. → Mitigation: round-trip tests assert
  byte-equality of unmodified sections. The existing `ConfigFileHelper`
  already handles the JsonElement coercion path; we extend its coverage,
  not rewrite it.

- [Vacuous audit] At the end of this change, the registry contains only
  three editors and the audit asserts a small surface. The audit's value
  scales with the next change. → Mitigation: the audit is wired now so
  that adding any editor in the next change automatically tightens the
  enforcement; no follow-up wiring step is required.

- [Secrets in `ExistingConfig`] The parsed `netclaw.json` may include
  schema fields that are themselves sensitive (e.g. allowed user IDs,
  email domains). → Mitigation: only `secrets.json` is exempted from
  context loading; non-secret PII present in `netclaw.json` is no more
  exposed than today. Section editors that render lists of IDs already
  display them in clear; this is unchanged.

- [Schema sections added without registry update] Future schema additions
  not in the exemption list and not bound to an editor would fail the
  audit immediately on their first PR. → Mitigation: this is the intended
  behavior. The exemption list is updated in the same PR that adds the
  schema section.

## Migration Plan

This change is internal-only and observable behavior is preserved for
first-run init. No data migration is required. The deploy story:

1. Land this change. `netclaw init` continues to behave identically for
   first-run installs; re-runs over existing config now pre-populate
   fields and merge on save (previously undefined).
2. The next change introduces `netclaw config`. No further migration
   needed.

Rollback: revert the change. `WizardContext.ExistingConfig` returns to its
declared-but-unused state. `WizardConfigBuilder` returns to overwrite.
First-run behavior is unaffected.

## Open Questions

None at execution time. All architectural decisions are locked above.
