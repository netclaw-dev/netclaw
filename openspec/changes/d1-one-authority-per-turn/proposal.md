## Why

A session buffers input that arrives while a turn runs. Before this change, one
batch could hold inputs from several requesters. The model call then ran under
the authority of one of them. A reminder or a child result could also abandon
an open approval that a person had not answered. Both rules break the
default-deny posture: one requester could spend the tools and the prompt
audience of another requester.

## What Changes

- A turn takes only the leading buffered inputs of one requester. Other inputs
  wait in order. Each starts its own turn under its own recorded authority.
- A running turn accepts a joining input only from the same requester.
- Automated input (reminder, child result, `SystemProcess`) never abandons an
  open approval. It waits and runs as its own turn after the approval closes. A
  message from a person still abandons the approval.
- Waiting input survives three events: a daemon restart (the session rebuilds
  it from the journal), an idle abandonment of a half-answered batch, and a
  supervisor restart of the session actor. Each input reaches the model once.
- A session may passivate while an input waits behind an approval. TA-11 keeps
  its rule that an unanswered request does not hold the session in memory.
- The deferred idle passivation keeps a session with a live background job in
  memory.
- **BREAKING (internal contract):** a mixed-requester follow-up no longer runs
  as one batch. It runs as separate turns. The pinned integration test
  `Buffered_messages_are_batched_into_follow_up_LLM_call` changed for this.

Source PRDs: `PRD-001` FR-002, FR-003, FR-011, and FR-017 (FR-017 exists only
on the `feature/coordination-cooperation-v2` stack; see `design.md`).

In scope: the session actor and its input flow, recovery of waiting input, and
the TA-11 abandonment rule. Out of scope: reminder admission and dedup (D2),
child result delivery (S2), a journal-only rebuild that removes
`AdmittedAuthority`, a hard stop while a tool runs, and the Expired close with
an unanswered `tool_use`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: add the requirement "One authority per turn for buffered input".
- `tool-authorization`: change TA-11 so only a person abandons an open approval, and automated input waits.
- `session-resume`: add the requirement that waiting input survives restart, idle abandonment, and an actor restart.
- `session-state-machine`: add the requirement for idle abandonment of a half-answered batch while input waits, and for passivation while input waits.
- `netclaw-scheduling`: add the requirement that a reminder that waits behind an approval is acknowledged and queued.

## Impact

- Code: `TurnContext` (`HasSameRequester`, `HasSameAuthority`), `SessionInputFlow`,
  `SessionExecutionLifetime`, `SessionToolWorkflow.Recovery`, `LlmSessionActor`,
  `ReminderExecutionActor` (one constant).
- Tests: accordant and actor specs in `src/Netclaw.Actors.Tests/Sessions/`, and a
  focused Stryker gate, `scripts/run-input-one-authority-mutations.sh`.
- Docs: `docs/architecture/tool-authorization.md`, `docs/spec/GLOSSARY.md`
  ("Requester"), `TOOLING.md`.
- Security: the change narrows authority. A buffered input never runs under a
  tool scope, trust level, or prompt audience that it did not carry. The journal
  stores no new field; recovery reads the recorded `InputAdmitted` authority.
- Operations: no config key, no schema change, no migration. A waiting input
  that existed only in memory before a restart is now rebuilt when an approval
  stays parked.
