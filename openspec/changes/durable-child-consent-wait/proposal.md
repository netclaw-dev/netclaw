## Why

A daemon restart stops every live child run. A config reload restarts the daemon too, for example when an operator
adds an MCP server (21 October 2026, 11:03 UTC). A child that waits for operator consent then loses its work, and the
session withdraws its prompt with "This approval request was withdrawn because Netclaw restarted before the task
finished. Nothing ran." (#2572).

The owner's rule for child consent is a steady state: the session waits for the person, with no timeout. A restart
breaks that steady state. This change keeps a waiting child across a restart, with the fewest new parts.

Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017 (durable child delegation) and FR-011 (tool access). Issue #2572.
Base: PR #2583 and PR #2584 (`child-approval-events-and-parent-pause` PR 1 and PR 2) are on `dev`.

## What Changes

- **Park always.** A child call that needs consent does not wait in memory. The child finishes the other calls of the
  batch, sends its park to the session, and stops. The live wait for child consent goes away.
- **The park holds all that the child needs.** The conversation, the open tool batch with the receipt facts of the
  finished calls, the path facts before the batch, the loop guard, the managed temporary retry targets, the spawn facts, and
  the consent requests.
- **The run record holds the park (PR 2).** The session journals `ChildRunEvent.Parked` in the run record, which is in
  the session journal and snapshot already. No new persistence ID, no child journal, and no purge.
- **One resume path.** When every request of a park has an answer, the session journals `ChildRunEvent.Resumed` and
  starts a new `SubAgentActor` from the park. Only the parked calls run, each at most once. A live answer and an answer
  after a restart use the same path.
- **Resume is a spawn from a recorded conversation.** The authority and the path facts come from the run record. The
  audience is the narrower of the recorded and the current trust. The model, the tools, the rules, and the project
  instructions resolve again. A tool fingerprint check stops an old answer from applying to a changed tool.
- **Recovery keeps parked runs.** A restart keeps the prompts of a parked run open, and the pause comes back. The
  session replays the open prompts to a new subscriber. A running child is lost, as today.
- **Supersedes.** This change replaces PR 3 (design D8) of `child-approval-events-and-parent-pause`. The owner
  decisions that change are in design.md, marked "pending owner confirmation".

In scope: background child runs of one session that park at consent, in every channel that supports interactive
approval, the TUI, and headless output.

Out of scope: the resume of a running (not parked) child after a restart, nested children, a consent timeout, session
retention (#2573), the headless exit rule (#2541), and a cross-process router.

Stack:

- PR 0 (base `dev`): pure refactors with no behavior change: the shared prompt builder, the shared re-drive helper,
  and the loop-guard export.
- PR 1a (base `feature/durable-child-consent-wait`): park and resume in memory: the transcript, the checkpoint, the
  loop guard, the pre-batch run scope, the resume gate, the answer binding, the fingerprint, the spawn failures, and
  the architecture document. No format change.
- PR 1b (same base): the receipt facts of finished calls, and the managed temporary retry target and denial directory.
  No format change. PR 1a must not reach `dev` alone. A separate reviewed PR merges the feature branch to `dev` after
  PR 1b lands.
- PR 2 (base `dev`): durable `Parked` and `Resumed`, recovery, passivation, the pause rule for the restart reminder,
  and prompt replay with dedupe in the channels. Needs #2587. Forward-only.
- PR 3 (base `dev`): TUI and headless dedupe, and the native smoke run.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: a child run parks at consent and resumes from its park; a parked run can be cancelled
  with no actor; the resume applies the spawn rules again.
- `netclaw-subagents`: the live approval wait and its watchdog pause are removed; consent outcomes settle once, at
  resume.
- `tool-authorization`: TA-12 keeps the prompt of a parked child valid after a restart; the resumed call runs once,
  under the recorded authority and the narrower current trust, and passes every gate again.
- `session-resume`: recovery keeps parked child runs, resumes an answered park, brings the pause back, and keeps one
  run of each held reminder.
- `channel-binding-parity`: an open child prompt replays to a new subscriber; each surface skips a call ID that it
  shows already; a replayed prompt that fails to post does not withdraw the request.

## Impact

- Code: `SubAgentActor`, `SubAgentSpawner`, `TurnStateTracker`, `ParentSessionApprovalBridge`,
  `SessionToolExecutionPipeline` (one shared plan-entry helper), `LlmSessionActor` and its `Children` partial,
  `SessionToolWorkflow` (`.Approvals`, `.Children`, `.Recovery`), `SessionState`, `BackgroundChildRun`,
  `SessionProtocol.ChildRuns`, `NetclawProtoMapper.CoordinationV2`, `ChannelOutputEngine`, the Slack, Mattermost, and
  Discord bindings, `ChatViewModel`, `HeadlessChannel`.
- Formats (PR 2, forward-only): two `fact` cases on `ChildRunEventV2Proto` (8, 9); a `parked` lifecycle case (21) and
  `prompts_issued` (22) on `BackgroundChildRunV2Proto`; four optional fields on `ToolApprovalRequestedProto` (25 to
  28); six new messages. No new snapshot field. No new persistence ID.
- Data: child transcripts go into `netclaw.db`, in the same data class as the parent transcript.
- Configuration: none.
- CI: two Stryker targets in the `child-consent` group: `child-resume-gate` (PR 1a, about 3 minutes) and
  `child-park-recovery` (PR 2, about 4 minutes; it also covers the park lines of the snapshot mapper).
- Docs: `docs/architecture/tool-authorization.md` sections 3.5, 3.6, 4, 7.5, 8, and 9; `docs/spec/GLOSSARY.md`; the
  `netclaw-operations` skill; release-note text for the downgrade.
- Prerequisite: #2587 before PR 2.

Security impact:

- A parked call runs only after a durable `Resumed` record, at most once.
- The resumed child takes its requester and path facts from the run record, never from the current turn or parent.
- The audience at resume is never wider than the recorded audience.
- The gate checks the parked call again under the current policy. A changed tool does not run on an old answer.
- Consent comes only from the journaled answer of the run requester. It is a one-time consent bound to the exact
  candidates of the prompted call.
- The parent model can never answer. The change adds no consent path.

Operational impact:

- A child that waits for consent survives a restart or a config reload. Its prompt stays live, and the pause comes
  back.
- A child prompt appears after the other calls of its batch finish.
- A park above 4 MiB ends the run with `park_too_large`.
- An older binary cannot recover a session that holds a park. The release notes say so.
