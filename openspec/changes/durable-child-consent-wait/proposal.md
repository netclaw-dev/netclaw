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
- **The run record holds the park.** The session journals one new event, `ChildRunEvent.Parked`. It carries the child
  conversation, the open tool batch, the consent requests, and the spawn facts that the child cannot get again. The
  run record is in the session journal and in the session snapshot already. No new persistence ID, no child journal,
  and no purge are necessary.
- **One resume path.** When every request of a park has an answer, the session journals `ChildRunEvent.Resumed` and
  starts a new `SubAgentActor` from the run record. Only the parked calls run, each once. A live answer and an answer
  after a restart use the same path.
- **Resume is a spawn from a recorded conversation.** The resume takes the authority from the run record. It resolves
  the model, the tools, the operating rules, and the project instructions again under the current configuration and
  policy, as a spawn does. The gate checks each parked call again at call time.
- **Recovery keeps parked runs.** A restart keeps the prompts of a parked run open. The pause comes back. A parked run
  whose requests all have answers, and that has no `Resumed`, resumes after recovery. A running child is lost, as
  today.
- **Prompt replay.** A new output subscriber gets each open child prompt again. The TUI, the channels, and headless
  output skip a call ID that they show already.
- **Supersedes.** This change replaces PR 3 (design D8) of `child-approval-events-and-parent-pause`: the child journal,
  the frozen tool set, the frozen model, and the purge. The owner must confirm this (see design, Open Questions).

In scope: background child runs of one session that park at consent, in every channel that supports interactive
approval, the TUI, and headless output.

Out of scope: the resume of a running (not parked) child after a restart, nested children, a consent timeout,
session retention (#2573), the headless exit rule (#2541), and a cross-process router.

Stack:

1. Park and resume while the daemon runs. A restart still withdraws a parked prompt. Forward-only.
2. Recovery keeps parked runs. The pause comes back. A parked session can passivate.
3. Prompt replay to a new subscriber, with dedupe.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: a child run parks at consent in its run record and resumes from it; a parked run can be
  cancelled with no actor.
- `netclaw-subagents`: the live approval wait and its watchdog pause are removed; consent outcomes settle once, at
  resume.
- `tool-authorization`: TA-12 no longer rejects the prompt of a parked child after a restart; the resumed call runs
  once, under the run authority, and passes every gate again.
- `session-resume`: recovery keeps parked child runs, resumes an answered park, and brings the pause back.
- `channel-binding-parity`: an open child prompt replays to a new subscriber, and each surface skips a call ID that it
  shows already.

## Impact

- Code: `SubAgentActor`, `SubAgentSpawner`, `ParentSessionApprovalBridge`, `LlmSessionActor` and its `Children`
  partial, `SessionToolWorkflow` (`.Approvals`, `.Children`, `.Recovery`), `SessionState`, `BackgroundChildRun`,
  `SessionProtocol.ChildRuns`, `NetclawProtoMapper.CoordinationV2`, `ChannelOutputEngine`, `ChatViewModel`,
  `HeadlessChannel`.
- Formats (forward-only from PR 1):
  - `ChildRunEventV2Proto` gets two `fact` cases: `parked` and `resumed`.
  - `BackgroundChildRunV2Proto` gets one `lifecycle` case: `parked`. It reuses `ChildRunApprovalProto`,
    `SerializableChatMessageProto`, and `ChildRunCheckpointProto`.
  - `ToolApprovalRequestedProto` gets `optional string display_text` and `optional bool is_messy` (PR 3, additive).
  - No new snapshot field. No new persistence ID.
- Configuration: none.
- CI: one new Stryker target, `child-resume-gate`, in the `child-consent` group (about 3 minutes).
- Docs: `docs/architecture/tool-authorization.md` sections 3.5, 3.6, 4, 8, and 9; `docs/spec/GLOSSARY.md` ("Parked
  child run"; "Child journal" is removed); the `netclaw-operations` skill.

Security impact:

- A parked call runs only after a durable `Resumed` record, at most once.
- The resumed child takes its authority from the run record, never from the current turn.
- The gate checks the parked call again under the current policy. A deny rule that the operator adds after the park
  wins over the answer.
- Consent comes only from the journaled answer of the run requester. It is a one-time consent bound to the exact
  candidates of the prompted call.
- The parent model can never answer. The change adds no consent path.

Operational impact:

- A child that waits for consent survives a restart or a config reload. Its prompt stays live, and the pause comes
  back.
- A child prompt appears after the other calls of its batch finish, not while they run.
- The session journal and snapshot hold the conversation of each parked child until it resumes.
- Rollout is forward-only from PR 1. Back up `netclaw.db` before the rollout.
