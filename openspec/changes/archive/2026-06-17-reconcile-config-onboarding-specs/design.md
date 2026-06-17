## Context

The `netclaw config` rewrite and `netclaw init` simplification are implemented, tested, and
shipped on `docs/netclaw-validated-ui-components`. A spec-vs-code audit found six canonical
specs drifted from the as-built behavior. This change is documentation-only: it edits the
affected specs' requirements so they describe what the code already does. There is no
implementation work — every delta cites the implementing type and/or test as evidence.

Constraint: deltas must copy MODIFIED requirement blocks verbatim from the existing spec
before editing, so no normative detail is lost at archive time; OpenSpec artifacts are
managed through the `/opsx-*` skills per the repo constitution.

## Goals / Non-Goals

**Goals:**
- Bring `netclaw-onboarding`, `channel-audience-tui`, `netclaw-config-command`,
  `security-posture-tui`, `feature-selection-wizard`, and `inbound-webhooks` in line with
  shipped behavior.
- Remove requirements describing abandoned approaches (the Memory/Memorizer init step) so
  they cannot mislead future work.
- Preserve security-relevant invariants by stating them as the code actually enforces them
  (inert unresolved channel names; auto-pairing the configuring client on non-local exposure).

**Non-Goals:**
- No production code, API, schema, or test changes — the implementation is already complete.
- No re-litigation of the shipped design decisions; only their spec record.
- The unimplemented Phase-2 onboarding features (environment discovery, project registration)
  are marked deferred, not removed — they remain future work outside this reconciliation.

## Decisions

- **MODIFIED in place over delete-and-readd.** Each drifted requirement is updated by copying
  its full block and editing the changed clauses, so unrelated normative detail and scenarios
  survive archiving. Delete-and-readd was rejected: it loses detail and muddies the diff.
- **REMOVE the Memory-provider requirements outright.** The Memorizer-vs-local-files step was a
  pre-build exploration that shipped as neither — memory is the always-on auto-memory system on
  SQLite, with no wizard step. It is REMOVED with Reason/Migration rather than MODIFIED, because
  no shipped behavior corresponds to it. Marking it "deferred" (as with the Phase-2 features)
  was rejected: there is no intent to build a memory wizard step.
- **Leave the bootstrap-exposure auto-pair requirement unchanged.** The audit flagged a
  spec/code mismatch (spec said auto-pair, code blocked); that was fixed in code
  (`ExposureModeStepViewModel.EnsureCurrentClientPaired`), so the existing spec is now accurate.
  Evidence: `ExposureModeConfigViewModelTests` — orphaned/empty/mismatched cases assert the
  configuring client is paired.

## Risks / Trade-offs

- [Memory-step removal reads as "memory was dropped"] → Mitigation: the REMOVED Reason states
  memory is the always-on SQLite auto-memory system; the Migration points to that subsystem.
- [Deltas are point-in-time snapshots that can re-drift] → Mitigation: each delta cites the
  implementing type/test; run `/opsx-verify` before archiving to confirm they still match code.
- [Imprecise MODIFIED header fails silently at apply] → Mitigation: copy `### Requirement:`
  headers verbatim from `openspec/specs/<cap>/spec.md`; validate before archive.

## Migration Plan

Spec-only change: merge with the implementation branch, then `/opsx-verify` and `/opsx-archive`
to sync the delta specs into `openspec/specs/`. Rollback is reverting the doc edits — no runtime
impact.
