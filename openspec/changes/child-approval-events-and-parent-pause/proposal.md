## Why

On 21 October 2026 a parent session started 5 background children with `spawn_agent`. Each child ran `for N in ...; do gh pr view $N ...; done`. The loop makes the command complex, so each child asked for one-shot consent. Five consent requests with no child name reached the Slack thread in 9 seconds.

The parent model did not know that the children waited for the operator. `check_agent_run` shows `pending_approval_count` only when the model polls. The parent decided that the children were stalled. It cancelled four children and started four new children. The prompts of the cancelled children kept live buttons (#2558). Each later click got the false reply "That approval prompt has expired — the session moved on or restarted".

Related open issues: #2558 (closed prompt keeps live buttons), #2541 (headless run waits with no reason), #2523 (TUI clears a child prompt at the end of the parent turn, so the child waits forever). Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-017 (durable child delegation) and FR-011 (tool access).

## What Changes

This change is a design for discussion. The owner made decisions 1 to 6 below. The design proposes the mechanics and lists open questions.

- **Parent notice.** When a child consent request opens or closes, the parent session gets one runtime notice. The notice uses the admitted-notice path of #2515. The session merges the events of a fan-out into one notice for each model boundary, so 5 children do not cause 5 parent model calls.
- **Runtime pause.** While a child consent request is open, the session pauses autonomous work. The current turn can finish. The session refuses `spawn_agent`, and an automated input does not start or join a turn. The pause lifts when the requests close or when the operator speaks.
- **Authoritative prompts.** Child prompts still go to the channel at once. The runtime renders them from the gate's consent request. The parent model can explain or cancel a child. It cannot answer, approve, or rewrite a prompt.
- **Named prompts.** Each prompt names the requesting child: the agent name and the run ID.
- **Withdrawal.** When a child run is cancelled, ends, times out, or is lost at restart, the runtime withdraws its open prompts. The channel redraws each prompt as closed. A later click gets an accurate reply, for example "withdrawn: the parent cancelled child run r-7f2".
- **Text answer guard.** A text answer ("A", "deny") resolves a prompt only when exactly one prompt is open for that sender. With two or more open prompts, the person must use the buttons.
- **TUI and headless.** A child prompt stays open in the TUI after the parent turn ends (#2523). A headless run states the reason when a child waits for consent (#2541).
- **Messaging seam.** The notice path becomes a closed set of runtime notice kinds. Inter-agent messages (next change) add one kind. This change does not design messaging.

In scope: background child runs of one session, all channels that support interactive approval, the TUI, and headless output.
Out of scope: inter-agent messages, nested children (`spawn_agent` stays denied to a child), one "approve for all" button, a TUI reopen that shows old prompts again, and a fatigue fix for literal shell loops.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `background-subagent-runs`: consent request notices, the pause, withdrawal on cancel or end, and accurate answers for withdrawn prompts.
- `tool-authorization`: TA-12 adds the named child prompt, the consent boundary for the parent model, the text answer guard, and the withdrawal reply.
- `netclaw-session`: a runtime notice kind set, and an input rule: an automated input waits while the pause is active.
- `netclaw-subagents`: a finite wait for child consent (if the owner accepts open question Q4).
- `session-resume`: recovery withdraws child prompts with a stated reason and closes stale approval notices without a turn.
- `channel-binding-parity`: a prompt-closed output that each channel binding redraws, and child prompts that stay open after a parent turn ends.

## Impact

- Code: `LlmSessionActor` and `LlmSessionActor.Children`, `SessionToolWorkflow` (`.Approvals`, `.Children`, `.Recovery`), `SessionInputFlow`, `SessionState.ChildRuns`, `ParentSessionApprovalBridge`, `SessionProtocol` outputs, the proto mapper, `ChannelOutputEngine`, `ApprovalResponseFlow`, the Slack, Discord, and Mattermost prompt builders, `ChatViewModel` (TUI), and the headless channel.
- Journal: `InputAdmitted` gets one new field for the approval notice kind. Older binaries cannot read the new rows. Back up `netclaw.db` before rollout.
- Configuration (if Q4 is accepted): one new `SubAgents` property and a schema update in the same PR.
- Docs: `docs/architecture/tool-authorization.md` sections 3.1, 3.6, 7.5, and 8 (see design). Two new glossary terms in `docs/spec/GLOSSARY.md`. The `agent-coordination` and `netclaw-operations` skills and their eval cases.

Security impact:

- The change adds a consent surface rule (the text answer guard) and a new owner for the pause decision. It adds no grant scope and no new way to give consent.
- A notice carries no text that a child wrote. A child cannot put instructions into the parent prompt through this path.
- The pause only refuses work. A fault in the pause can stop or delay work. It cannot grant authority.

Operational impact:

- The operator sees which child asks, and a closed prompt has no live buttons.
- A forgotten child prompt can hold automated inputs of the session (reminders, job results) until the prompt closes. Q4 bounds this time.
