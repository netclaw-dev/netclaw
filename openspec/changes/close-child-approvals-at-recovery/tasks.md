## 1. Recovery log

- [ ] 1.1 Skip the `Tool approval requested` entry in the `Recovering` phase and add the `recovered` field. Verify `RecoveredApprovalLogTests.Recovery_logs_only_the_open_call_once_with_recovered_true` passes.
- [ ] 1.2 Add `SessionToolWorkflow.OpenApprovals()`. In `RecoveryCompleted`, close child entries, then log each open entry with `recovered=True`. Verify the same test passes and `RecoveredApprovalLogTests.A_late_answer_to_a_closed_call_runs_no_tool` still passes.
- [ ] 1.3 Remove `ExpireRecoveredChildApprovals()` from `RecoverChildren`. Verify the late-answer theory passes.

## 2. Durable close event

- [ ] 2.1 Replace the test-first stub of `ChildApprovalsClosed` with the final record, add `ChildApprovalsClosedProto`, the mapper pair, and manifest `cac-v1` in `NetclawProtobufSerializer`. Verify `ChildApprovalsClosedRoundTripTests` pass.
- [ ] 2.2 Make `ExpireChildApprovals` return whether it removed an entry. Persist one event on cancel, on terminal result, and at recovery. Verify `ChildRunActorBoundaryTests.Cancelling_a_child_with_an_open_approval_persists_one_close_event` and `RecoveredApprovalLogTests.Recovery_persists_a_close_event_for_a_stranded_child_approval` pass.
- [ ] 2.3 Replace the stub of `SessionToolWorkflow.ApplyChildApprovalsClosed` and register `Recover<ChildApprovalsClosed>`. Verify `ChildApprovalCloseMutationTests` pass.

## 3. Gates

- [ ] 3.1 Add markers around the target span in `SessionToolWorkflow.Children.cs`, add `scripts/run-child-approval-close-mutations.sh`, set the expected mutant count, add the gate to the `child-owner-and-report` group in `pr_validation.yml`, and add a row to `TOOLING.md`. Verify the script reports all mutants killed.
- [ ] 3.2 Update `netclaw-operations` only if a log or CLI text that the skill names changes. Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`.
