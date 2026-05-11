## Context

The archived multi-speaker attribution change already established the core trust
model: only the current authorized message is executable, while the adopted
window is quoted context captured for audit and model grounding. The regression
investigation showed that later consumers could read "adopted context exists"
as a proxy for "third-party adopted context exists," which is incorrect for
self-only adopted history and causes policy drift in memory formation.

This clarification change keeps the architecture intact. Threaded adapters still
hydrate an adopted window, the session still persists the exact adopted record
and canonical projection, and approval surfaces still report adopted provenance.
The design work here is about making each consumer read the same facts the same
way.

## Goals / Non-Goals

**Goals:**

- Define one stable meaning for `HasAdoptedContext`: any non-empty adopted
  window.
- Define one stable meaning for adopted-speaker provenance: all sender ids in
  that adopted window.
- Introduce a distinct third-party adopted policy concept derived by comparing
  adopted sender ids against the current authorized author.
- Keep automatic memory suppression tied to third-party adopted context rather
  than to adopted context in general.
- Preserve the current security model that adopted context is quoted and
  non-executable.

**Non-Goals:**

- Changing which messages become adopted.
- Allowing adopted content to execute tools, slash commands, jobs, reminders, or
  memory writes directly.
- Changing approval UX, ACL rules, or watermark mechanics beyond the clarified
  metadata contract.
- Implementing the runtime changes in this planning turn.

## Decisions

### 1. Split factual provenance from policy interpretation

**Decision:** All persisted and surfaced adopted-context provenance remains a
literal description of the adopted window: if the window is non-empty,
`HasAdoptedContext=true` and adopted-speaker provenance lists every sender id in
that window, even if the only sender is the current author.

**Rationale:** Audit and security artifacts should answer "what context was
adopted?" rather than "what policy outcome did we infer?" Overloading
`HasAdoptedContext` to mean "third-party present" makes self-only adopted
history invisible to audit surfaces and invites contradictory interpretations
across subsystems.

**Alternatives considered:**

- Reinterpret `HasAdoptedContext` to mean "contains a non-self sender." Rejected
  because it makes the audit trail incomplete and breaks the archived
  multi-speaker attribution contract.
- Keep one overloaded flag and document call-site-specific interpretation.
  Rejected because the regression came from exactly that ambiguity.

### 2. Add a derived third-party adopted policy concept

**Decision:** Introduce `HasThirdPartyAdoptedContext` as a derived policy signal
that is true when any sender id in the adopted window differs from the current
authorized author of the executable message.

**Rationale:** This preserves the current trust model while giving policy
consumers a direct, explicit input for "someone else participated in the adopted
window." The derivation is deterministic and can be computed from persisted
truthful provenance without mutating the underlying facts.

**Alternatives considered:**

- Store only the sender-id set and force every consumer to derive the policy on
  its own. Rejected because repeated ad hoc derivation is how drift reappears.
- Introduce per-message trust tiers for every adopted line. Rejected as broader
  than the regression requires.

### 3. Automatic memory suppression follows third-party policy only

**Decision:** Automatic memory-formation suppression, caution, or other policy
branching that exists because adopted context might represent somebody else's
words SHALL key off `HasThirdPartyAdoptedContext`, not `HasAdoptedContext`.

**Rationale:** Self-only adopted history still reflects quoted, non-executable
context, but it does not create the cross-speaker provenance concern that the
memory regression was trying to guard against. Tying suppression to the derived
third-party signal restores the intended policy boundary without hiding the full
adopted window.

### 4. Approval and security provenance stay fully inclusive

**Decision:** Approval prompts, stored approval context, and session audit
artifacts continue to report adopted-context presence whenever the adopted
window is non-empty and continue to list all adopted sender ids. These surfaces
may also carry `HasThirdPartyAdoptedContext` as a separate policy field, but
they SHALL NOT trim or reinterpret the full adopted provenance.

**Rationale:** Approval and security reviews need the whole adopted window to
understand what background was present when the current authorized message ran.
The truthful record and the derived policy bit serve different purposes and both
need to survive.

## Risks / Trade-offs

- **[Risk]** Future implementation updates one metadata producer but not all
  consumers. -> **Mitigation:** define the semantics in five affected
  capabilities and require tests that cover self-only and third-party adopted
  windows across adapter, session, approval, and memory paths.
- **[Risk]** Reviewers may mistake the new policy concept for a trust-model
  relaxation. -> **Mitigation:** every affected spec repeats that adopted context
  remains quoted and non-executable; only suppression logic changes.
- **[Trade-off]** More metadata fields means slightly more persistence and test
  surface. -> **Mitigation:** the separation removes ambiguity and prevents more
  expensive policy regressions.

## Migration Plan

This change is clarification-only for now. The eventual implementation should:

1. Extend the runtime metadata shape so `HasAdoptedContext` and
   `HasThirdPartyAdoptedContext` are both explicit where needed.
2. Backfill or derive the new policy field for any already-persisted
   adopted-context records if those records are reused across restarts.
3. Update automatic memory formation tests before enabling the behavior change.

Rollback is specification-only at this stage: revert the change plan if the team
decides to preserve the ambiguous contract.

## Open Questions

- None for the change-plan phase. The implementation phase can decide whether
  `HasThirdPartyAdoptedContext` is persisted directly everywhere or derived on
  read in some internal models, so long as the observable contract remains
  consistent.
