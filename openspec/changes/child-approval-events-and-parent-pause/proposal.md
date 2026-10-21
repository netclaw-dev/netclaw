## Why

On 21 October 2026 a parent session started 5 background children with `spawn_agent`. Each child ran `for N in ...; do gh pr view $N ...; done`. The loop makes the command complex, so each child asked for one-shot consent. Five consent requests reached the Slack thread in 9 seconds.

The parent model did not know that the children waited for the operator. `check_agent_run` shows `pending_approval_count` only when the model polls. The parent decided that the children were stalled. It cancelled four children and started four new children. The prompts of the cancelled children kept live buttons (#2558). Each later click got the false reply "That approval prompt has expired — the session moved on or restarted".

The same day a config reload (a new MCP server) restarted the daemon. A restart stops every child, so a child that waits for consent loses its work and its prompt (#2572).

Related open issues: #2558, #2541 (headless run waits with no reason), #2523 (TUI clears a child prompt at the end of the parent turn), #2572. Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017 (durable child delegation) and FR-011 (tool access).

## What Changes

The owner and the coordinator decided the rules on 21 October 2026. The design gives the mechanics.

- **Agent event channel.** A typed inter-agent event channel: a closed family of immutable `AgentEvent` records with a minimal envelope (`Sender`, `Target`, `SentAt`). The first members are `ChildApprovalRequested`, `ChildApprovalResolved` (approved, refused, or withdrawn with a reason), and `ChildRunEnded` (the #2515 child result, moved in memory; field 14 stays on disk). Messaging tools come later as new members of the same family.
- **Bridge vs channel.** The approval bridge stays the one consent loop. The channel carries observation and messages, never consent.
- **Parent notice.** When child consent requests open, or when all requests of the session close, the parent gets a runtime notice that the runtime renders from the typed events. The session keeps one digest and merges the events of a fan-out into one notice for each model boundary. An idle parent wakes once for each notice. The notice is an ordinary admitted input with source kind and sender `child-consent`, principal `VerifiedAutomation`, and authenticity `LocalProcess`. The journal format does not change. The notice turn cannot raise a consent request, and it cannot check or cancel a person's run.
- **Runtime pause.** While any child consent request is open, the whole session pauses autonomous work. The current turn can finish. The session refuses `spawn_agent`, and an automated input does not start or join a turn. A person's message does not end the pause. The pause ends only when every open request closes. No request expires.
- **Reminders while paused.** A current-session reminder fire waits. A later fire of the same reminder replaces it. When the pause ends, at most one turn of each reminder runs.
- **No child identity in user-facing text.** The prompt shows only the command and the scope, with buttons only. The closed redraw, the late-click reply, and headless output do not name the child. The parent notice names the agent and the run.
- **Prompt lifetime.** A child prompt stays open in channels and in the TUI after the parent turn ends or fails (#2523).
- **Withdrawal.** When a child run is cancelled, ends, or is lost, when its prompt cannot be posted, or when a non-parked child is lost at restart, the runtime withdraws its open prompts. The channel redraws each prompt as closed. A later click gets an accurate reply, for example "This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran."
- **Text answer guard.** A text answer ("A", "deny") resolves a prompt only when exactly one prompt is open for that sender.
- **Durable child wait (#2572).** A child run becomes an Akka.Persistence actor with its own persistence ID, `subagent-{escapedSessionId}/{runId}`, which stays out of the session list. It writes its journal at its first park only. The first event freezes the exact system prompt, the resolved provider and model, the tool set, the project instructions, the operating rules, and the findings flag. A child that needs consent persists and stops (park always). When every parked prompt has an answer, also an answer that came before the park, the session recreates the child from its journal under the authority of the run record. Only the parked calls run, once each. The parent snapshot holds parked prompt entries, so compaction does not lose them. A missing frozen model, provider, or agent definition ends the run as lost, with an honest notice. The parent purges the child journal last.

In scope: the channel contract, background child runs of one session, all channels that support interactive approval, the TUI, headless output, and the durable child wait.
Out of scope: messaging tools, a cross-process router, nested children, an "approve for all" prompt, a consent timeout, the resume of a running child after a restart (the next change; the child journal supports it), session retention (#2573), one reminder rule for every wait (deferred), and a fatigue fix for literal shell loops.

Stack (each of PR 1 and PR 2 ships and rolls back alone):

1. Channel types, notices, pause, reminder rule, prompt lifetime, and the TUI and channel turn-end change.
2. Withdrawal, the prompt-closed redraw, withdrawal texts, buttons-only child prompts, and the text answer guard.
3. The durable child wait (#2572). Forward-only.

The spec deltas describe the state after PR 3. The change syncs to the main specs after PR 3 merges.

## Capabilities

### New Capabilities

- `agent-event-channel`: the typed event family, the minimal envelope, the owner-stamped sender, the rule that a payload has no agent-chosen text, ordering and delivery guarantees, model rendering, and the split between the bridge and the channel.

### Modified Capabilities

- `background-subagent-runs`: the authority rule for a resumed run; consent notices to the parent; the pause; withdrawal; the parked lifecycle with resume; the purge.
- `tool-authorization`: TA-12 adds the buttons-only prompt with command and scope, the consent boundary for the parent model, the text answer guard, the withdrawal reply, and a parked child's prompt that survives a restart.
- `netclaw-session`: the `child-consent` input kind and principal; an automated input waits while the pause is active; a reminder fire into a paused session waits, and a later fire replaces it.
- `netclaw-subagents`: the execution contract, progressive tool disclosure, and run-scoped working context for a parked child; the child journal; park always; consent outcomes at resume. The watchdog-during-approval and live outcome requirements are removed.
- `session-resume`: the parent snapshot keeps parked requests; recovery keeps parked child requests, resumes answered parks, withdraws the others with a stated reason, closes pending approval notices, and purges ended child journals.
- `channel-binding-parity`: the text answer guard in the shared flow, prompt lifetime at turn end and turn error, buttons-only child prompts, the prompt-closed redraw, and replay dedupe in channels and the TUI.

## Impact

- Code: `LlmSessionActor` and `LlmSessionActor.Children`, `SessionToolWorkflow` (`.Approvals`, `.Children`, `.Recovery`), `SessionInputFlow`, `SessionState.ChildRuns`, `BackgroundChildRun`, `ParentSessionApprovalBridge`, `SubAgentActor`, `SubAgentSpawner`, `IChatClientProvider`, `SessionProtocol` outputs, the proto mapper, `ChannelOutputEngine`, `ApprovalResponseFlow`, `PendingApprovalLookup`, the Slack, Discord, and Mattermost prompt builders, `ChatViewModel` (TUI), and the headless channel.
- Formats:
  - PR 1: an optional `lifetime` field on `PendingApprovalPromptTracked`. An old binary ignores it. A row without it reads as a turn prompt.
  - PR 2: none.
  - PR 3: child journal types, three parent `ChildRunEvent` cases, a `Parked` lifecycle case and a `HasChildJournal` flag, and parked prompt entries in `SessionSnapshotProto`. Each new type gets a proto message, a manifest, and a mapper entry. Forward-only. Back up `netclaw.db` before the PR 3 rollout.
- Models: PR 3 adds an exact provider and model lookup with no role fallback. It touches the same seam as the open change `subagent-explicit-model-selection`.
- Configuration: none.
- CI: a new Stryker matrix group, `child-consent`, with four gates (about 12 minutes).
- Docs: `docs/architecture/tool-authorization.md` sections 3.1, 3.5, 3.6, 4, 7.5, 8, and 9 (see design). Glossary terms in `docs/spec/GLOSSARY.md`. The `agent-coordination` and `netclaw-operations` skills and their eval cases.

Security impact:

- Only the session that owns a run builds an event about it, so a child can emit only about itself.
- An event payload has no agent-chosen text, and a notice carries no text that a child wrote.
- The channel never carries consent. The parent cannot approve.
- The text answer guard closes a consent spoof among parallel prompts.
- A parked child's call runs only after a durable resume record, once, under the authority of the run record, and it passes every gate check again under the current policy. The model and the tool set never change silently.
- The notice turn is automation. It cannot check or cancel a person's run, and a call in it that needs consent is denied without a prompt. A webhook that claims the `child-consent` kind is not a notice, because its authenticity is not `LocalProcess`.
- The pause only refuses work. A fault in the pause can delay work. It cannot grant authority.

Operational impact:

- A closed prompt has no live buttons, and a late click gets the true reason.
- A session with an open child request stays paused until the operator answers or cancels. Nothing expires.
- A child that waits for consent survives a restart or a config reload. A waiting session can passivate.
