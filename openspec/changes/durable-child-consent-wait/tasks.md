The work has three code PRs, all to `dev`: PR 0, PR 1, and PR 2. Each task names its PR. PR 2 comes after PR #2592
(#2587). The owner cut prompt replay, the tool fingerprint, PR 1b, PR 3, the feature branch, and the merge PR on
22 October 2026 (design, "Cuts").

Each behavior PR starts with failing tests. A test author first adds stub types (`ChildRunPark`, `ChildParkReached`,
`ChildEventRejected`, the `RunSubAgent.Resume` field, and an owner handler that keeps today's behavior), so that each
new test compiles and fails on an assertion, not on a compile error. Each test must fail on the base of its PR. No test
uses `Thread.Sleep` or `Task.Delay`. Tests use Akka.TestKit probes, `AwaitAssertAsync`, a fake `TimeProvider`, a fake
chat client, and a recording tool. Each test class that owns a temp folder deletes it (`DisposableTempDir`,
`DisposeAsync`).

## 1. Gate before code

- [x] 1.1 (spec PR) Get the owner answer on each decision in design Context, "Owner decisions". Verify the design records each answer as confirmed by the owner, 22 Oct 2026.
- [ ] 1.2 (spec PR) With the OpenSpec skills, remove design D8, tasks 7 to 9, and the child journal, park, purge, and parked-recovery spec deltas from `child-approval-events-and-parent-pause`, then sync and archive it. Verify `openspec validate --strict` passes for both changes.

# PR 0: pure refactors (no behavior change)

## 2. PR 0

- [ ] 2.1 (PR 0) Move the prompt construction of `ParentSessionApprovalBridge.PromptAsync` into `ApprovalRequestBuilder` with two entry points, `ForSessionCall` and `ForChildCall`; `ForChildCall` runs the requester check (`EnsureAuthorityContext`). Verify the existing parent and child approval tests pass with no change.
- [ ] 2.2 (PR 0) Move "apply one plan entry to a call context" out of `SessionToolExecutionPipeline` (`:538-575`) into `ApprovalRedriveEntry` (M11). Verify the existing re-drive tests pass with no change.
- [ ] 2.3 (PR 0) Add `TurnStateTracker.ExportLoopGuard` and `RestoreLoopGuard` with `TurnLoopGuardState`, and the round-trip test `TurnStateTrackerTests.An_exported_loop_guard_restores_the_same_decisions`. Verify the new test and the existing tests pass.
- [ ] 2.4 (PR 0) Add `ChildConsentPause.Holds(TurnContext authority, bool isChildNotice)` as the one pause predicate; make `SessionInputFlow.IsHeldByPause` and `WasHeldByPause` call it. Add `ChildConsentPauseTests.The_drain_and_the_recovery_path_hold_the_same_inputs` over one set of inputs (a person, `UntrustedExternal`, `VerifiedAutomation`, `SystemProcess`, a child result notice, an approval notice, a webhook that claims the notice kind). Verify the test and the existing pause tests pass.
- [ ] 2.5 (PR 0) Run slopwatch and the header check. Verify both pass and the PR changes no behavior test.

# PR 1: park and resume in memory (no format change)

## 3. PR 1 tests first

The owner approved a cut plan for PR 1 on 22 October 2026 (design, "PR 1 cut plan"). It replaces the earlier test
list with one parameterized test for each lifecycle family and table tests for the pure decisions.

- [x] 3.1 (PR 1) Add the stub types. Verify the solution builds and every PR 1 test below compiles.
- [x] 3.2 (PR 1) U1: `SubAgentActorParkTests.The_park_holds_the_child_state_and_the_child_waits_for_the_commit` (the transcript, the scope directories, the loop guard, and the skill overlay; the child waits for `ChildTerminalCommitted`). Verify it fails.
- [x] 3.3 (PR 1) U2: `SubAgentActorParkTests.A_resume_runs_only_the_parked_call_and_keeps_the_round_the_guard_and_the_overlay`, rows approved and refused. Verify it fails.
- [x] 3.4 (PR 1) U3 and U4: `ChildParkRulesTests.Park_acceptance_follows_the_owner_and_the_lifecycle` and `The_resume_gate_waits_for_every_answer_and_for_the_drain` (tables). Verify they fail.
- [x] 3.5 (PR 1) S1 to S4 in `ChildParkSpecs`, each row through `AssertRunInvariantsAsync`: park and resume (5 rows), refusals (3 rows), cancel (2 rows), and restart (2 rows; PR 2 changes both). Verify they fail.
- [ ] 3.6 (later PR, with `FileActivity`) Add `SubAgentActorParkTests.A_finished_project_change_applies_at_the_park` and `A_parked_project_change_wins_over_a_finished_one`. Verify they fail.
- [ ] 3.7 (later PR, with `DeferredMedia` and the managed temporary retry target) Add a test for the deferred media of a park and for the retry target and denial hint of a parked managed temporary retry. Verify it fails.
- Moved: the size cap tests go to PR 2 (task 7.x). The `check_agent_run` "parked" state and the narrower audience (M6) are cut.

## 4. PR 1 implementation

- [x] 4.1 (PR 1) In `SubAgentActor`, keep a `SerializableChatMessage` list next to `_history`. On `ToolApprovalRequiredException` of an owned child, return a parked marker. At the batch end, apply the effects of the finished calls, build the park, end the run, Tell `ChildParkReached`, and wait for `ChildTerminalCommitted`. Remove the live wait, `RequestConsentAsync`, and the watchdog approval states. Verify task 3.2 passes.
- [x] 4.2 (PR 1) In `LlmSessionActor.Children`, decide each park with `ChildParkRules.AcceptPark`; keep the park actor-local; number the prompts; build each prompt with `ApprovalRequestBuilder.ForChildCall`; release the child. Put the `ChildEventRejected` branch first in `OnPersistRejected`. A refused park calls `RecordChildTerminal(Failed)` with an existing reason. Verify tasks 3.4 and 3.5 (S2, S3) pass.
- [x] 4.3 (PR 1) Route every child answer to the handler with no live claim. Add `TryResumeParkedRun` with `ChildParkRules.ResumeGate`, and `SubAgentSpawner.PrepareResume` with the spawn rules under `run.OriginalContext`. Start the new incarnation with `RunSubAgent { Resume }` and the prompt counter in the actor name. In the child, restore the transcript and the loop guard, seed the round, and run only the parked calls with `ApprovalRedriveEntry`. Verify tasks 3.3 and 3.5 (S1) pass.
- [x] 4.4 (PR 1) Cancel of a parked run calls `RecordChildTerminal(Cancelled)` directly. A session with a park does not passivate. Verify task 3.5 (S3, S4) passes.
- [ ] 4.5 (PR 2) Apply the answer to the park in the persist callback, and add the size cap and its log.
- [ ] 4.6 (later PR) Add `DeferredMedia`, `FileActivity`, and the managed temporary retry target (`platform_temporary_root` on `ApprovalRedriveEntry`).

## 5. PR 1 docs and gates

- [x] 5.1 (PR 1) Update `docs/architecture/tool-authorization.md` for the parked child prompt. Verify the doc links resolve.
- [x] 5.2 (PR 1) Add the `child-resume-gate` Stryker target over `ChildParkRules.AcceptPark` and `ChildParkRules.ResumeGate` to the `child-consent` group, with a `TOOLING.md` row and its script. Verify all mutants are killed.
- [x] 5.3 (PR 1) Update the `netclaw-operations` skill for a parked child and bump `metadata.version`.
- [x] 5.4 (PR 1) Run slopwatch and the header check. Verify both pass.

# PR 2: durable park and recovery (forward-only)

## 6. PR 2 prerequisite

- [ ] 6.1 (PR 2) Confirm PR #2592 (#2587) is merged on `dev`, and rebase PR 2 on it, because the N1 rule edits `TryResumeInterruptedInput` too. Verify the #2587 regression test passes on the PR 2 base.

## 7. PR 2 tests first

- [ ] 7.1 (PR 2) Add `ChildRunParkSerializationTests`: `Parked` (fact 8), `Resumed` (fact 9), the `parked` lifecycle (21), `prompts_issued` (22), and `platform_temporary_root` (25) round-trip; a record without them reads as before. Verify they fail on PR 1.
- [ ] 7.2 (PR 2) Add `ChildRunParkSnapshotMapperTests.Every_park_field_survives_the_snapshot_mapper`: build a parked run record with a non-default value in each park field and in `prompts_issued`; map it to `SessionSnapshotV2Proto` and back; compare structurally (deep equivalence over list members). Add `Every_park_field_is_set_in_the_builder`: walk the built park recursively by reflection and assert that every property and list element is non-default. Verify both fail.
- [ ] 7.3 (PR 2) Add `ChildParkFailureSpecs.A_damaged_loop_guard_ends_the_run_with_park_damaged_at_resume` (a negative count, `RefusedRepeatCount` above `EscalatedRefusedRepeat + 1`, `EqualRounds` outside 0 to 2): recovery completes with the run parked; the answer runs `PrepareResume`, which ends the run `Failed` with `park_damaged`, and no parked call runs. Verify it fails.
- [ ] 7.4 (PR 2) Change the two PR 1 tests of task 3.11 to the PR 2 rules, and add `ChildParkRecoverySpecs.A_restart_keeps_a_parked_prompt_open_and_a_click_resumes_the_call_once`, `A_wrong_user_after_recovery_is_rejected_from_the_run_record`, and `A_snapshot_and_a_journal_give_the_same_open_entries` (one recovery path). Verify they fail.
- [ ] 7.5 (PR 2) Add `ChildParkRecoverySpecs.A_compaction_snapshot_keeps_the_park_and_its_answers` and `A_routine_snapshot_is_not_blocked_by_parked_child_entries`. Verify they fail.
- [ ] 7.6 (PR 2) Add `ChildParkRecoverySpecs.A_crash_after_the_last_answer_resumes_at_recovery`, `A_crash_after_Resumed_records_the_run_lost_with_the_uncertain_text`, `A_rejected_Resumed_ends_the_run_failed_and_starts_no_actor`, `An_answer_during_a_drain_in_Ready_resumes_after_recovery`, and `An_answer_during_drain_passivation_gets_the_restart_nack_and_the_prompt_stays_open`. Verify they fail.
- [ ] 7.7 (PR 2) Add `ChildParkRecoverySpecs.The_pause_comes_back_and_a_held_fire_runs_once_after_the_last_answer`, `A_stashed_fire_during_recovery_replaces_the_held_fire_and_the_reminder_runs_once`, `A_damaged_held_input_is_logged_and_recovery_completes`, `A_pending_notice_at_the_crash_gives_one_new_notice_with_the_open_parked_requests`, and `A_restart_withdraws_only_the_prompts_of_runs_that_did_not_park`. Verify they fail.
- [ ] 7.8 (PR 2) Add `ChildParkRecoverySpecs.A_restart_reminder_does_not_run_held_input_while_paused` and `A_restart_reminder_still_resumes_an_input_that_the_pause_does_not_hold`; add the restart-resume path to the pause predicate test as `The_drain_recovery_and_restart_resume_paths_hold_the_same_inputs`. Verify they fail.
- [ ] 7.9 (PR 2) Add `ChildParkRecoverySpecs.A_parked_run_with_an_open_request_gives_no_restart_reminder`, `A_parked_run_with_all_answers_gives_a_restart_reminder`, `A_session_with_only_parked_children_passivates_and_a_click_wakes_it`, and `A_lost_parked_run_never_says_uncertain`. Verify they fail.
- [ ] 7.10 (PR 2) Add `ChildParkSqliteRecoveryTests.A_parked_run_survives_a_SQLite_journal_and_snapshot_restart`. Verify it fails.
- [ ] 7.11 (PR 2) Add `ChildParkRecoveryCostTests.Recovery_with_parks_at_the_cap_stays_within_the_measured_bound` on the SQLite journal: several parks at the 4 MiB cap against a plain session with the same parent history; a stopwatch up to the reply of a probe message that the session stashes during recovery (no sleep); the fastest of several runs of each. Measure on the CI runner first, then record the numbers and the bound (a wide ratio plus a small floor) as constants with a comment. Verify five CI runs pass in a row, and verify once on a local run that the test fails when the bound is below the measured ratio. Add no test-only hook to production code.
- [ ] 7.12 (PR 2) Add `ChildParkRecoverySpecs.Recovery_logs_the_count_and_bytes_of_the_parked_runs` and `Recovery_with_no_parks_writes_no_park_line`. Verify they fail.

## 8. PR 2 implementation

- [ ] 8.1 (PR 2) Add `ChildRunEvent.Parked`, `ChildRunEvent.Resumed`, `ChildRunLifecycle.Parked`, `PromptsIssued`, `ChildRunParkProto`, `ChildLoopGuardProto`, and the fields of the design with manifests and mapper entries (the mapper only maps); add the loop-guard spawn rule (`park_damaged`) to `PrepareResume`; build the `Accepted` checkpoint from the park when `Resumed` applies; apply `ToolApprovalResolved` of a parked run to its park, live and replay. Verify tasks 7.1 to 7.3 pass.
- [ ] 8.2 (PR 2) Persist `Parked` in place of the actor-local park; persist `Resumed` before the spawn; on a `Resumed` rejection send `ChildEventRejected(runId, resume_not_recorded)`. Verify task 7.6 passes.
- [ ] 8.3 (PR 2) In `RecoveryCompleted`, split pending inputs once with `SplitByAuthority`; build the open entries from `_state.ChildRuns` for the journal and the snapshot alike; withdraw only entries of runs with no park; add the `Parked` cases to `RecoverChildren` and `LostChildResult`; restore held input when the pause holds; write the `child_parks_recovered` line. Verify tasks 7.4, 7.5, 7.7, and 7.12 pass.
- [ ] 8.4 (PR 2) In `TryResumeInterruptedInput`, leave out held input through `ChildConsentPause.Holds` while `IsChildConsentPaused`, and acknowledge with no turn when no input that the pause does not hold remains. Verify task 7.8 passes.
- [ ] 8.5 (PR 2) Change `AllowsSnapshot`, `HasUnreportedChildren`, and the restart-reminder count for parked runs. Verify tasks 7.5, 7.9, and 7.10 pass, and run task 7.11 on the CI runner to set its bound.

## 9. PR 2 docs and gates

- [ ] 9.1 (PR 2) Update `docs/architecture/tool-authorization.md` 3.6 and 4 (a parked prompt survives a restart; no replay). Verify the doc links resolve.
- [ ] 9.2 (PR 2) Add the `child-park-recovery` Stryker target (the `RecoverChildren` decision and the child-entry rule of `AllowsSnapshot`; no mapper span) to the `child-consent` group with a `TOOLING.md` row that names task 7.2 as the guard of the mapper. Verify all mutants are killed and the group stays under the job timeout.
- [ ] 9.3 (PR 2) Add the downgrade text of the design to the release notes draft, and update the `netclaw-operations` skill (bump `metadata.version`). Verify `./evals/run-evals.sh` passes.
- [ ] 9.4 (PR 2) Run slopwatch and the header check. Verify both pass.

## 10. Finish

- [ ] 10.1 (after PR 2) Sync this change to the main specs and archive it. Verify `openspec validate --strict` passes on the main specs.
