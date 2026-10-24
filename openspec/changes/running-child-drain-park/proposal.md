## Why

A graceful daemon stop on 23 October 2026 ended 9 running child runs as `owner_restarted`. None of the 9 waited for
consent, so the durable park of `durable-child-park-recovery` (PR 2 of #2572) did not apply to them (#2655). A running
child that stops at a safe point can use the same park record and the same recovery, so the restart costs it no work.

Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017 (durable child delegation). Issue #2655. Base:
`feature/durable-child-pr2` (#2670), which makes the consent park durable. The PR 2 plan names this change as its
follow-up.

## What Changes

- **The drain tells each running child to park.** At a graceful drain, the session sends one park signal to each live
  child. The child starts no new model call and no new tool batch after the signal.
- **The child parks at the next safe point.** A child that runs a tool batch parks when the batch ends. A child that
  waits for a model response cancels the call and parks at once, because a model call has no outside effect. The park
  has no consent request. The term for it is a drain park.
- **The parent records the park.** The session journals the existing `ChildRunEvent.Parked` record with zero requests.
  The reducer accepts a park with zero requests. A batch that ends with a call that needs consent makes a consent park,
  as in PR 2.
- **The drain acknowledgement waits for the children, with a bound.** The session acknowledges the drain only after no
  live child remains, or after the child wait ends. The drain request carries that wait. It is the existing drain
  deadline minus a short acknowledgement margin, so the session keeps its restart reminder.
- **Recovery resumes the park with the PR 2 rules.** The resume gate, the recovery action, and the `Resumed` record do
  not change. A drain park has no open request, so the gate resumes it at recovery. The resumed child starts with a
  model call.
- **Losses stay explicit.** A child that does not reach a safe point inside the wait is lost (`owner_restarted`), as
  today. Its running tool never runs again.

In scope: background child runs that run (not parked) at a graceful drain (config reload or daemon stop), in every
channel.

Out of scope: a crash or a kill with no drain, a child of a parent turn that keeps no channel type, a round or a
tool-call limit for child runs (#2656, none on purpose), a model-call grace for the child, and an expiry for a drain
park.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: a running child parks at a graceful drain and resumes once after the restart under its
  recorded authority.
- `session-resume`: the drain acknowledgement waits for live children inside a bound that the drain request carries.

## Impact

- Code: `PrepareForDaemonRestart`, `SessionDrainHelper`, `SessionExecutionLifetime`, `LlmSessionActor` (drain and
  `Children` partial), `ChildParkRules`, `SessionState` (child-run reducer), `SubAgentActor`.
- No new journal format. The park uses `ChildRunEvent.Parked` (fact 8) and lifecycle field 21 of PR 2.
- CI: the `child-resume-gate` Stryker span takes one new pure rule (`RequesterCanAnswer`).
- Docs: `docs/architecture/tool-authorization.md` 3.6, `docs/spec/GLOSSARY.md` ("parked child run"), the
  `netclaw-operations` skill (`references/child-runs.md`).

Security impact:

- No call runs twice. A drain park holds no parked call. A tool that runs at the end of the wait leaves the run in
  `Accepted`, and recovery records it as lost and does not start it again.
- The resumed child runs under `run.OriginalContext` only. The tool gate checks each later call of the child.
- A drain park asks nobody, so it needs no requester who can answer a prompt. A consent park still needs one.

Operational impact:

- A graceful drain can take up to the drain deadline (20 s today) when a child runs. Before this change, a drain did
  not wait for children.
- The session logs one Warning when the child wait ends with live children. It names the count.
- Forward-only, as PR 2: a binary with PR 2 and without this change fails recovery of a journal that holds a drain park.
