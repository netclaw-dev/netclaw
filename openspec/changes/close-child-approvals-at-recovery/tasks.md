## 1. Recovery log

- [x] 1.1 Skip the `Tool approval requested` entry in the `Recovering` phase and add the `recovered` field. Verify `RecoveredApprovalLogTests.Recovery_logs_only_the_open_call_once`, `A_recovered_journal_with_only_an_answered_call_logs_nothing`, and `A_live_request_says_that_it_is_not_recovered` pass.
- [x] 1.2 Add `SessionToolWorkflow.OpenApprovals()`. In `RecoveryCompleted`, close the child entries, then log each open entry with `recovered=True`. Verify `The_line_for_the_open_call_says_that_recovery_found_it` passes.

## 2. Close rule

- [x] 2.1 Replace the stub of `SessionToolWorkflow.CloseChildApprovals(ChildRunEvent)`: close every entry of the run for a cancel or terminal record, and nothing for other records. Verify `ChildApprovalCloseMutationTests` pass.
- [x] 2.2 Call it from the live cancel and terminal Persist callbacks, in place of `ExpireChildApprovals(runId)`, and from `Recover<ChildRunEvent>`. Verify `RecoveredApprovalLogTests.Replay_of_a_closed_child_approval_logs_nothing` and `ChildRunActorBoundaryTests.A_cancelled_child_approval_is_closed_for_replay_and_logs_nothing_after_a_restart` pass.
- [x] 2.3 Move `ExpireRecoveredChildApprovals()` from `RecoverChildren` into `RecoveryCompleted`, before `TransitionTo(Ready)`. Verify `A_late_answer_to_a_closed_child_call_finds_no_entry`, `A_late_answer_to_a_closed_call_runs_no_tool`, and `An_old_journal_recovers_and_the_session_snapshots_again` pass.

## 3. Gates

- [x] 3.1 Add markers around the target span in `SessionToolWorkflow.Children.cs` (`CloseChildApprovals` and `ExpireRecoveredChildApprovals`), add `scripts/run-child-approval-close-mutations.sh`, set the expected mutant count, add the gate to the `child-owner-and-report` group in `pr_validation.yml`, and add a row to `TOOLING.md`. Verify the script reports all mutants killed.
- [x] 3.2 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Update `netclaw-operations` only if a log or CLI text that the skill names changes.
