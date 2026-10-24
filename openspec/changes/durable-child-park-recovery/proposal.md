## Why

PR 1 of `durable-child-consent-wait` (#2618) parks a child that waits for consent, but it keeps the park in memory. A
restart, a config reload, or passivation still loses the run, and the person gets "Nothing ran" (#2572). This change is
PR 2: it writes the park to the run record, so a parked run survives these events and a click resumes the call once.

Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017 (durable child delegation) and FR-011 (tool access). Issue #2572.
Base: `feature/durable-child-pr1` (PR 1), rebased on `dev` after #2651 (#2632, the continuation input of a stopped
turn). The plan has the owner decisions of 23 October 2026.

## What Changes

- **The run record holds the park.** The session journals `ChildRunEvent.Parked` (fact 8) with the park, its consent
  requests, in one write. It journals `ChildRunEvent.Resumed` (fact 9) before a resumed child
  starts. The snapshot holds a parked run as a new lifecycle case (field 21) and the prompt counter (field 22).
- **The answer goes into the park.** The persist callback of an answer applies it to the park, so a compaction
  snapshot keeps the answer.
- **Recovery keeps a parked run.** The behavior is in `durable-child-consent-wait` ("Recovery keeps parked child
  runs"). This change adds the rules that that change does not state.
- **Reasons.** A rejected `Resumed` reuses `tool_execution_failed` with the text "The parked calls did not run." A
  damaged loop guard ends the run with the new reason `park_damaged` at the answer. Recovery completes.
- **A late click.** A click after a resume and a later snapshot gets the closed-call text, not "already answered".
  Nothing runs either way.
- **Forward-only.** An older binary (0.28.0-beta.2 or PR 1 alone) fails recovery of a session that holds a park row.
  The release notes say so, and the swap rule restores the backup.
- **#2651 contract.** Each input admission closes a pending continuation input that no turn selected, in the same
  journal write: an approval notice (`AdmitConsentNoticeThen` through `PersistReplacingInput`), a reminder fire that
  replaces a waiting fire (`ReplaceWaitingFire`), and the result notice of a live child that recovery records as lost
  (`owner_restarted`). The pause holds an automation continuation until the last answer, and then it runs once.
- **Supersedes.** This change replaces tasks 6 to 9 of `durable-child-consent-wait`. That change keeps its PR 0 and
  PR 1 tasks, its design, and its spec deltas.

In scope: background child runs of one session that park at consent, in every channel that supports interactive
approval.

Out of scope: a RUNNING child at a restart (#2655), prompt replay, `DeferredMedia`, the managed temporary retry target
(`platform_temporary_root`), the `check_agent_run` "parked" state, M6 (a narrower audience at resume), and the round
limit of child runs (#2656).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: the park and the resume are durable in the run record; a damaged park ends at the
  answer; park records are forward-only.
- `session-resume`: each admission path closes a pending continuation input; the pause holds an automation
  continuation until the last answer.

## Impact

- Code: `SessionState` (reducer), `BackgroundChildRun`, `ChildRunEvent`, `ChildRunLifecycle`, `ChildRunPark`,
  `ChildParkRules`, `NetclawProtoMapper.CoordinationV2`, the coordination proto, `LlmSessionActor` and its `Children`
  partial, `SessionToolWorkflow` (`.Approvals`, `.Children`, `.Recovery`), `SessionInputFlow`, `SubAgentSpawner`.
- CI: the `child-resume-gate` span grows over the new `ChildParkRules` functions, in the existing child gate.
- Docs: `docs/architecture/tool-authorization.md` 3.6 and 4, `docs/spec/GLOSSARY.md`, the `netclaw-operations` skill
  (`references/child-runs.md`), `TOOLING.md`, and the release-note text.

Security impact:

- A parked call runs only after a durable `Resumed` record, at most once. A crash after `Resumed` records the run as
  lost, and the call does not run again.
- The requester of a parked prompt comes from the run record after a restart. Another person's click is rejected.
- The gate checks the parked call again under the current policy.
- A park above 4 MiB ends with `park_too_large`, and no prompt appears.

Operational impact:

- A child that waits for consent survives a restart, a config reload, a compaction snapshot, and passivation. A
  session with only open parked runs can passivate, and a click wakes it.
- Recovery writes `child_parks_recovered count={n} bytes={b}` only when n > 0.
- A rollback to an older binary needs the verified backup when the journal holds a park row (swap rule kinds
  `PARK-EVENT` and `PARK-SNAPSHOT`). The first swap runs one canary.
