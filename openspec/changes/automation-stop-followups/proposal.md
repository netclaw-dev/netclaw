## Why

PR #2630 lets a waiting person end a running automation turn at the next tool-batch end. Its review left the
follow-ups of issue #2633. The person's turn gets no fact that the automation turn stopped unfinished. A compaction
that is due at the end point does not run, so the person's first model call can overflow. A person who writes during a
tool-loop compaction waits for one more automation model call and one more tool batch. No main spec states the rule.
The #2634 investigation also found that the "Conversation compaction" requirement promises a threshold check after
tool-result clearance that the pipeline never had.

Source: `docs/prd/PRD-001-netclaw-mvp.md` FR-002 (turn processing), FR-004 (conversation compaction), and FR-016
(restart coordination). Issues #2633 and #2634. Base: `feature/continuation-followups` (PR #2676, #2659).

## What Changes

- **The person's turn gets a stop note.** When a person input ends an automation turn, the first model request of
  the person's turn holds one fixed note: the automated turn stopped unfinished because a person sent a message. The
  note tells the model to answer the person and not to continue the stopped work unless the person asks.
- **A due compaction runs at the end point.** When a person input ends an automation turn and a compaction is due at
  that tool batch, the session closes the automation turn, commits the compaction, and then starts the person's turn.
- **A waiting person ends the turn at a tool-loop compaction end.** After a tool-loop compaction, a waiting person
  ends the automation turn instead of the tool-loop resume. The automation turn makes no further model call. A restart
  request still wins. An overflow replay keeps its turn.
- **One main requirement states the rule.** `netclaw-session` gets "A person ends an automation turn".
- **The compaction requirement matches the code.** Tool-result clearance never skips the extractive reduction.

In scope: the end of an automation turn by a person input, the compaction at that point, the tool-loop compaction end,
and the stop note.

Out of scope: a person check during a model call, a new compaction trigger for failed turns, a kind label in the note,
and a round limit for turns or child runs (#2656).

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: add "A person ends an automation turn". Change "Conversation compaction" so that tool-result
  clearance does not skip the reduction.

## Impact

- Code: `SessionInputFlow.AdvanceAfterCompaction`, `InputProgressAction`, the tool-batch end and the compaction end in
  `LlmSessionActor`, and the per-turn context of `SessionModelContext` and `SessionMessageAssembler`.
- Tests: `PersonInputEndsAutomationTurnSpecs` (orchestration) and `ChildConsentPauseMutationTests` (pure decision).
- Eval: one new case, `automation_stop_note_person_first`, in `evals/run-evals.sh`.
- Docs: `docs/architecture/tool-authorization.md` (the person check moves to the compaction end too) and the
  `netclaw-operations` skill.

Security: the person's turn keeps its own recorded authority. The note is fixed text. It holds no sender-controlled
text, because a webhook sets its own source kind. The note tells the model not to continue automation work under the
person's authority without a request.

Operations: a person who stops a long reminder loop gets an answer sooner. The session log keeps
`turn_person_input_end_at_tool_batch`. A compaction at the end point logs as a normal compaction.
