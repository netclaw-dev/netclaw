The test author writes groups 1 and 2 first, against compile shells. A different agent implements groups 3 to 6 and
does not edit the tests. No test uses `Thread.Sleep` or `Task.Delay`. Each test deletes the temp files it creates.

## 1. Compile shells

- [x] 1.1 Add `SessionProtocol.RestartExpiredInstruction` (public constant) and `InputAdmitted.ContinuesStoppedTurn` (bool, not mapped, never set). Verify the solution builds.

## 2. Tests (each row named; verify each listed row fails on an assertion before group 3)

- [x] 2.1 `RestartDrainSpecs.A_restart_drain_meets_the_drain_contract`: rows `ExpiredReminderAfterToolBatch`, `ExpiredReminderAtFirstModelCall`, `EmptyInputAtFirstModelCall`, `EmptyInputJoinsToolLoop`, `EmptyInputBuffered`. The shared helper checks the marker on the continuation of every resumable row, and that no resumed model request holds an empty user message unless a person sent one.
- [x] 2.2 `ChildParkRecoverySpecs.A_restart_keeps_the_child_consent_pause`: rows `continuation-no-reminder` and `continuation-expired`. The shared postcondition adds: no parent model request holds an empty user message.
- [x] 2.3 `ChildParkSpecs.A_restart_keeps_a_durable_park` row `damaged`: a late click gets `approval_withdrawn` (gap A).
- [x] 2.4 `ChildDrainParkSpecs.A_drain_acknowledges_from_the_journal_callback_of_the_last_child_record` row `park-rejected` (gap B).
- [x] 2.5 `ChildRunActorBoundaryTests.A_cancelled_child_approval_is_closed_for_replay_and_logs_nothing_after_a_restart` rows `durable-park` and `legacy-request` (gap C, guard).
- [x] 2.6 `ContinuationMarkerTests`: the classifier table and the journal and snapshot table.
- [x] 2.7 `RestartRecoveryServiceTests.StartAsync_registers_fresh_reminders_and_expiry_signals_for_expired_ones`.

## 3. The marker (implementation)

- [x] 3.1 Add `optional bool continues_stopped_turn = 15` to `InputAdmittedProto`, write it always, and read an absent field with the legacy shape rule. Verify `ContinuationMarkerTests` passes.
- [x] 3.2 Set the marker in `KeepStoppedTurnThen`. Make `IsContinuation` and `ApplyBufferedBatch` read it. Copy `AdoptedSpeakerIds` as normal admission does. Verify the `RestartDrainSpecs` marker and empty-input rows pass.

## 4. The expiry signal (implementation)

- [x] 4.1 `RestartRecoveryService` registers an expired restart reminder as the expiry signal. Verify task 2.7 passes.
- [x] 4.2 The session closes the resumable inputs on the expiry signal, posts the notice once, and replies `RestartResumeNotNeeded`. Verify rows `ExpiredReminderAfterToolBatch`, `ExpiredReminderAtFirstModelCall`, and `continuation-expired` pass.

## 5. The held continuation (implementation)

- [x] 5.1 `QueueInputsHeldByThePause` leaves out continuation inputs. A restart reminder during the pause queues the held continuation. Verify rows `continuation-no-reminder` and `automation-continuation` pass.

## 6. Refusal outcomes (implementation)

- [x] 6.1 A refused resume after the answer read leaves the answers unused. Verify task 2.3 passes and row `unbound` still passes.
- [x] 6.2 `ChildEventRejected` tells a rejected drain park from a consent park. Verify task 2.4 passes and S2 row `rejected` still passes.

## 7. Docs and gates

- [ ] 7.1 Update the `netclaw-operations` skill (restart reminder expiry and notice) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [x] 7.2 Run slopwatch, the header check, and `openspec validate continuation-restart-followups --strict`. Verify all pass.
- [ ] 7.3 After the merge: archive this change after `durable-child-park-recovery`. Verify `openspec validate --strict` passes on the main specs.
