## Why

On 21 October 2026 a parent session started 5 background children with `spawn_agent`. Each child ran `for N in ...; do gh pr view $N ...; done`. The loop makes the command complex, so each child asked for one-shot consent. Five consent requests reached the Slack thread in 9 seconds.

The parent model did not know that the children waited for the operator. `check_agent_run` shows `pending_approval_count` only when the model polls. The parent decided that the children were stalled. It cancelled four children and started four new children. The prompts of the cancelled children kept live buttons (#2558). Each later click got the false reply "That approval prompt has expired — the session moved on or restarted".

The same day a config reload (a new MCP server) restarted the daemon. A restart stops every child, so a child that waits for consent loses its work and its prompt (#2572).

Related open issues: #2558, #2541 (headless run waits with no reason), #2523 (TUI clears a child prompt at the end of the parent turn), #2572. Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017 (durable child delegation) and FR-011 (tool access).

## What Changes

The owner decided the rules on 21 October 2026. The design gives the mechanics.

- **Agent event channel.** This change starts a typed inter-agent event channel: a closed family of immutable `AgentEvent` records with a minimal envelope (`Sender`, `Target`, `SentAt`). The first members are `ChildApprovalRequested`, `ChildApprovalResolved` (approved, refused, or withdrawn with a reason), and `ChildRunEnded` (the #2515 child result, moved in memory; field 14 stays on disk). Messaging tools come later as new members of the same family.
- **Bridge vs channel.** The approval bridge stays the one consent loop. The channel carries observation and messages, never consent.
- **Parent notice.** When child consent requests open or all close, the parent gets a runtime notice that the runtime renders from the typed events. The session merges the events of a fan-out into one notice for each model boundary. An idle parent wakes once for each notice. The notice is an ordinary admitted input with a new source kind. The journal format does not change.
- **Runtime pause.** While any child consent request is open, the whole session pauses autonomous work. The current turn can finish. The session refuses `spawn_agent`, and an automated input does not start or join a turn. A person's message does not end the pause. The pause ends only when every open request closes. No request expires.
- **Reminders while paused.** A current-session reminder fire waits. A later fire of the same reminder replaces it. When the pause ends, at most one turn of each reminder runs.
- **No child identity in user-facing text.** The prompt shows only the command and the scope. The closed redraw and the late-click reply do not name the child. The parent notice names the agent and the run.
- **Withdrawal.** When a child run is cancelled or ends, or a non-parked child is lost at restart, the runtime withdraws its open prompts. The channel redraws each prompt as closed. A later click gets an accurate reply, for example "This approval request was withdrawn because the task that asked for it was cancelled."
- **Text answer guard.** A text answer ("A", "deny") resolves a prompt only when exactly one prompt is open for that sender.
- **TUI and headless.** A child prompt stays open in the TUI after the parent turn ends (#2523). A headless run states the reason when a child waits for consent (#2541).
- **Durable child wait (#2572).** A child run becomes an Akka.Persistence actor with its own persistence ID, `subagent-{escapedSessionId}/{runId}`, which stays out of the session list. Its journal holds the child conversation. Its first event freezes the exact system prompt and model. A child that needs consent persists and stops (park always). When the last parked prompt has an answer, the session recreates the child from its journal, and only the parked calls run, once each. The wait survives a restart and a config reload. The prompt stays live, and the pause comes back. A frozen model that no longer exists ends the run as lost, with an honest notice. The parent purges the child journal last: after the run end, `result.md`, and the notice.

In scope: the channel contract, background child runs of one session, all channels that support interactive approval, the TUI, headless output, and the durable child wait.
Out of scope: messaging tools, a cross-process router, nested children, an "approve for all" prompt, a consent timeout, the resume of a running child after a restart (the next change; the child journal supports it), session retention (#2573), one reminder rule for every wait (deferred), and a fatigue fix for literal shell loops.

Stack: (1) channel types, notice digest, pause, and reminder rule; (2) withdrawal, prompt lifetime, channel and TUI redraw, and the text answer guard; (3) the durable child wait.

## Capabilities

### New Capabilities

- `agent-event-channel`: the typed event family, the minimal envelope, the owner-stamped sender, the rule that a payload has no free text, ordering and delivery guarantees, model rendering, and the split between the bridge and the channel.

### Modified Capabilities

- `background-subagent-runs`: consent notices to the parent, the pause, withdrawal, the child result as a channel member, and the parked lifecycle with resume and recovery.
- `tool-authorization`: TA-12 adds the prompt that shows only command and scope, the consent boundary for the parent model, the text answer guard, the withdrawal reply, and a parked child's prompt that survives a restart.
- `netclaw-session`: the `child-consent` input kind, and an input rule: an automated input waits while the pause is active.
- `netclaw-scheduling`: a reminder fire into a paused session waits, and a later fire replaces it.
- `netclaw-subagents`: the child journal, its persistence ID, the frozen system prompt and model, and park always.
- `session-resume`: recovery keeps parked child requests, resumes answered parks, withdraws the others with a stated reason, and purges ended child journals.
- `channel-binding-parity`: a prompt-closed output that each binding redraws, child prompts that stay open after a parent turn ends, and replayed prompts that are not posted twice.

## Impact

- Code: `LlmSessionActor` and `LlmSessionActor.Children`, `SessionToolWorkflow` (`.Approvals`, `.Children`, `.Recovery`), `SessionInputFlow`, `SessionState.ChildRuns`, `BackgroundChildRun`, `ParentSessionApprovalBridge`, `SubAgentActor`, `SubAgentSpawner`, `SessionProtocol` outputs, the proto mapper, `ChannelOutputEngine`, `ApprovalResponseFlow`, `PendingApprovalLookup`, the Slack, Discord, and Mattermost prompt builders, `ChatViewModel` (TUI), and the headless channel.
- Journal: PRs 1 and 2 have no format change. PR 3 adds a child journal for each child run (new `SubAgentRunProtocol` events and a snapshot type) and three parent `ChildRunEvent` cases (`Parked`, `Resumed`, `JournalPurged`) with a `Parked` lifecycle case. Each new type gets a proto message, a manifest, and a mapper entry. That change is forward-only. Back up `netclaw.db` before the PR 3 rollout.
- Models: PR 3 adds an exact model lookup with no role fallback for a resumed child. It touches the same seam as the open change `subagent-explicit-model-selection`.
- Configuration: none.
- Docs: `docs/architecture/tool-authorization.md` sections 3.1, 3.6, 7.5, and 8 (see design). New glossary terms in `docs/spec/GLOSSARY.md`. The `agent-coordination` and `netclaw-operations` skills and their eval cases.

Security impact:

- Only the session that owns a run builds an event about it, so a child can emit only about itself.
- An event payload has no free text, and a notice carries no text that a child wrote.
- The channel never carries consent. The parent cannot approve.
- The text answer guard closes a consent spoof among parallel prompts.
- A parked child's call runs only after a durable resume record, once, and it passes every gate check again under the current policy. The model never changes silently.
- The pause only refuses work. A fault in the pause can delay work. It cannot grant authority.

Operational impact:

- A closed prompt has no live buttons, and a late click gets the true reason.
- A session with an open child request stays paused until the operator answers or cancels. Nothing expires.
- A child that waits for consent survives a restart or a config reload. A waiting session can passivate.
