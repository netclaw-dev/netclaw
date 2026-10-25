The test author writes groups 1 and 2 first, against compile shells. A different agent implements groups 3 to 6 and
does not edit the tests. No test uses `Thread.Sleep` or `Task.Delay`. Each test deletes the temp files it creates.

## 1. Compile shells

- [x] 1.1 Add `InputProgressAction.EndTurn` (no producer yet). Verify the solution builds.

## 2. Tests (each row named; verify each listed failing row fails on an assertion before group 3)

- [x] 2.1 `PersonInputEndsAutomationTurnSpecs.A_person_input_ends_a_running_automation_turn_at_the_next_safe_boundary`: the shared helper adds the stop note check (one note in each request of the person's turn, before the person's message; no note in other rows) and the tool-pair check. Failing rows: `ReminderTurnThenPerson`, `NoticeTurnThenPerson`, `BackgroundJobTurnThenPerson`, `DueCompactionAtTheEndPoint`, `PersonDuringToolLoopCompaction`. Guard rows: `PersonTurnThenOtherPerson`, `ReminderTurnThenReminder`, `PersonTurnThenSamePerson`.
- [x] 2.2 `ChildConsentPauseMutationTests.A_person_input_ends_an_automation_turn_at_a_safe_boundary`: the table gets the compaction boundary. Failing rows: `CompactionReminderThenPerson`, `CompactionNoticeThenPerson`, `CompactionReminderThenInsider`. Guard rows: `CompactionReminderThenStranger`, `CompactionPersonThenOtherPerson`, `CompactionReminderThenChildResult`, `CompactionReminderThenReminder`, `CompactionRestartWins`, `CompactionReplayKeepsTurn`.
- [x] 2.3 Eval case `automation_stop_note_person_first` in `evals/run-evals.sh`. Run it with `NETCLAW_EVAL_CASE=automation_stop_note_person_first ./evals/run-evals.sh` after group 5.

## 3. The compaction end (implementation)

- [x] 3.1 `AdvanceAfterCompaction` returns `EndTurn` for a tool-loop resume with an automation owner and a waiting person, after the restart check (design D1). Verify the compaction rows of task 2.2 pass.
- [x] 3.2 The actor handles `EndTurn` at the compaction end like the tool-batch end. Verify row `PersonDuringToolLoopCompaction` passes on its turn and call checks.

## 4. The end-point compaction (implementation)

- [x] 4.1 At `ToolBatchNextAction.EndTurn`, compact after the close when `ShouldCompact()` is true (design D2). Do not add a compaction check to the failed-turn path. Verify row `DueCompactionAtTheEndPoint` passes its compaction order check.

## 5. The stop note (implementation)

- [x] 5.1 Keep one actor-local pending note. Set it at each stop. Put it in the per-turn context block of the first call of the next person turn, then clear it (design D3). Keep the note text as one C# string literal in `src/Netclaw.Actors/Sessions/`, because the eval reads it from source. Verify all rows of task 2.1 pass.

## 6. Docs and gates

- [x] 6.1 Update `docs/architecture/tool-authorization.md`, section "One authority per turn": the check runs at the tool-batch end and at the tool-loop compaction end, a due compaction runs before the person's turn, and the person's turn gets the note.
- [x] 6.2 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` with the stop note and bump `metadata.version`.
- [x] 6.3 Add the new compaction-end stop call to `scripts/run-input-one-authority-mutations.sh` with its measured count, after task 2.2 kills its removal mutant. Verify the script passes.
- [ ] 6.4 Run `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`, and `openspec validate automation-stop-followups --strict`. Run the full eval suite, because the prompt changed.
