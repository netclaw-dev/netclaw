The stack has three code PRs on `feature/durable-child-consent-wait`. Each PR starts with failing tests. A test author
writes the tests of a group first and verifies that they fail on the base of that PR. No test uses `Thread.Sleep` or
`Task.Delay`. Tests use Akka.TestKit probes, `AwaitAssertAsync`, a fake `TimeProvider`, a fake chat client, and a
recording tool. Each test class that owns a temp folder deletes it (`DisposableTempDir`, `DisposeAsync`).

## 1. Gate before code

- [ ] 1.1 Get the owner decision on design Open Questions 1 to 5. Verify the answers are recorded in design.md Context.
- [ ] 1.2 With the OpenSpec skills, remove design D8, tasks 7 to 9, and the child journal, park, purge, and parked-recovery spec deltas from `child-approval-events-and-parent-pause`, then sync and archive it. Verify `openspec validate --strict` passes for both changes.

# PR 1: park and resume while the daemon runs

## 2. PR 1 tests first

- [ ] 2.1 Add `ChildRunParkSerializationTests`: `ChildRunEvent.Parked` and `Resumed`, the `Parked` lifecycle in `SessionSnapshotV2Proto`, `PromptsIssued`, and the `display_text` and `is_messy` fields of `ToolApprovalRequestedProto` round-trip; a run record with no `PromptsIssued` reads 0; an unknown lifecycle case fails loud. Verify they fail on `dev`.
- [ ] 2.2 Add `SubAgentActorParkTests.The_park_holds_the_open_batch_and_no_message_between_results`: a batch with a parked call and a sibling that returns media and a repeat nudge; assert the transcript ends with the assistant message, the sibling result is in `SettledResults`, and no nudge is in the transcript. Verify it fails.
- [ ] 2.3 Add `SubAgentActorParkTests.A_resume_seeds_one_time_consent_only_for_the_parked_call_id` and `A_refused_parked_call_returns_the_refusal_text_and_never_dispatches`: the recording tool sees one dispatch for the approved call ID and none for the refused one; the results join in call order before the media nudge and the repeat nudge. Verify they fail.
- [ ] 2.4 Add `ChildParkSpecs.A_child_parks_after_its_siblings_end_and_the_prompt_follows_the_park`: assert the journal probe sees `Parked` before the `ToolInteractionRequest` output, the sibling tool ran once, the child actor terminated, and no lost terminal record exists. Verify it fails.
- [ ] 2.5 Add `ChildParkSpecs.An_approval_resumes_the_parked_call_once_from_the_run_record`: assert `ToolApprovalResolved`, then `Resumed`, then the one tool dispatch, in that order; the second model call gets the transcript plus both results; the sibling does not run again. Verify it fails.
- [ ] 2.6 Add `ChildParkSpecs.Two_parked_calls_run_only_after_both_answers` and `A_second_park_uses_new_prompt_numbers_and_a_new_actor_name`. Verify they fail.
- [ ] 2.7 Add security specs: `ChildParkSecuritySpecs.A_resumed_child_uses_the_run_authority_while_another_requester_turn_runs` (Bob's turn runs; the tool context has Alice's requester and audience), `A_deny_rule_added_after_the_park_wins_over_the_answer` (no dispatch, access-denied result), `Another_requester_cannot_answer_a_parked_prompt` (nack `approval_wrong_requester`, no `ToolApprovalResolved`), and `A_new_call_after_resume_is_not_covered_by_the_old_consent` (a second park). Verify they fail.
- [ ] 2.8 Add failure specs: `ChildParkFailureSpecs.A_resume_with_a_degraded_provider_ends_failed_and_runs_no_parked_call` (reason `model_unavailable`, notice states it), `A_resume_with_subagents_disabled_ends_failed`, `A_parked_call_to_a_removed_tool_gets_unknown_tool_and_the_child_continues`, and `An_answer_after_Resumed_gets_already_answered_and_runs_nothing`. Verify they fail.
- [ ] 2.9 Add control specs: `ChildParkControlSpecs.A_cancel_of_a_parked_run_ends_it_cancelled_with_one_write_and_no_actor`, `A_park_for_a_cancelling_run_is_refused_and_the_run_ends_cancelled`, and `check_agent_run_reports_parked_with_the_open_count`. Verify they fail.
- [ ] 2.10 Add PR 1 restart and idle specs: `ChildParkSpecs.A_restart_withdraws_a_parked_prompt_with_DaemonRestarted_in_PR1` and `A_session_with_a_parked_child_does_not_passivate_in_PR1`. PR 2 changes both on purpose. Verify they fail on `dev`.

## 3. PR 1 implementation

- [ ] 3.1 Add `ChildRunPark`, `ChildRunEvent.Parked`, `ChildRunEvent.Resumed`, `ChildRunLifecycle.Parked`, and `BackgroundChildRun.PromptsIssued`; add the proto cases and fields, manifests, and mapper entries; apply `ToolApprovalResolved` of a parked run to its park. Verify task 2.1 passes.
- [ ] 3.2 Move the prompt construction of `ParentSessionApprovalBridge.PromptAsync` into one shared builder for parent and child prompts; remove `RequestConsentAsync` and the child `Interlocked` numbering. Verify the existing parent approval tests pass.
- [ ] 3.3 In `SubAgentActor`, record a parked marker on `ToolApprovalRequiredException`, build the park at the batch end, Ask the owner with `ChildParkReached` (the `CommitCheckpointAsync` pattern), and stop after the ack; remove the live wait and its watchdog states. Verify task 2.2 passes.
- [ ] 3.4 In `LlmSessionActor.Children`, accept a park: check ownership and `Accepted`, number the prompts, persist `Parked`, unwatch and release the child, ack, open the entries, emit the prompts, and update the digest. Verify task 2.4 passes.
- [ ] 3.5 Route every child answer to one handler with no live claim; add `TryResumeParkedRun` with the open-request check, the drain check, `SubAgentSpawner.PrepareResume`, `Resumed`, and the incarnation name; build the re-drive plan with `ApprovalRedrivePlanBuilder` keyed by `OriginalChildCallId`. Verify tasks 2.3, 2.5, 2.6, 2.7, and 2.8 pass.
- [ ] 3.6 Add the `Parked` cases to `ControlChild`, `ChildStatus`, and `RecordChildTerminal`; keep the PR 1 restart and idle rules. Verify tasks 2.9 and 2.10 pass.

## 4. PR 1 docs and gates

- [ ] 4.1 Update `docs/architecture/tool-authorization.md` 3.5 (the park in the run record), 3.6 (child prompts appear after the park; every child answer takes the resume path), 4 (one child call end to end), and 9 (the resume gate row). Verify the doc links resolve.
- [ ] 4.2 Update `docs/spec/GLOSSARY.md`: rewrite "Parked child run" for the run record and remove "Child journal". Verify no doc links to the removed term.
- [ ] 4.3 Add the `child-resume-gate` Stryker target to the `child-consent` group with a `TOOLING.md` row and its script. Verify all mutants are killed and the group stays under the job timeout.
- [ ] 4.4 Update the `netclaw-operations` skill (a parked child, `check_agent_run` state `parked`, the resume failure reasons) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [ ] 4.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

# PR 2: recovery keeps parked runs

## 5. PR 2 tests first

- [ ] 5.1 Change the two tests of task 2.10 to the PR 2 rules and add `ChildParkRecoverySpecs.A_restart_keeps_a_parked_prompt_open_and_a_click_resumes_the_call_once` (no `ToolInteractionClosed` for the parked call; the session is paused after recovery). Verify they fail on PR 1.
- [ ] 5.2 Add `ChildParkRecoverySpecs.A_compaction_snapshot_keeps_the_park_and_its_answers` (p1 answered, compaction, journal delete, restart, p2 answered, both calls run once) and `A_routine_snapshot_is_not_blocked_by_parked_child_entries`. Verify they fail.
- [ ] 5.3 Add `ChildParkRecoverySpecs.A_crash_after_the_last_answer_resumes_at_recovery`, `A_crash_after_Resumed_records_the_run_lost_and_does_not_run_the_call_again`, and `An_answer_during_a_restart_drain_resumes_after_recovery`. Verify they fail.
- [ ] 5.4 Add `ChildParkRecoverySpecs.The_pause_comes_back_and_a_held_fire_runs_once_after_the_last_answer`, `A_pending_notice_at_the_crash_gives_one_new_notice_with_the_open_parked_requests`, and `A_restart_withdraws_only_the_prompts_of_runs_that_did_not_park`. Verify they fail.
- [ ] 5.5 Add `ChildParkRecoverySpecs.A_session_with_only_parked_children_passivates_and_a_click_wakes_it`. Verify it fails.
- [ ] 5.6 Add `ChildParkSqliteRecoveryTests.A_parked_run_survives_a_SQLite_journal_and_snapshot_restart`. Verify it fails.

## 6. PR 2 implementation

- [ ] 6.1 Apply the parked requests to `SessionToolWorkflow` on `Parked` replay, and add `HydrateParkedChildApprovals` after a snapshot offer. Verify task 5.2 passes.
- [ ] 6.2 Withdraw at recovery only the entries of runs that are not parked; keep `_restartWithdrawals` for those; let `WasPausedAtRecovery` count parked runs; resume an answered park in `RecoverChildren`. Verify tasks 5.1, 5.3, and 5.4 pass.
- [ ] 6.3 Let `AllowsSnapshot` ignore child entries and let a parked run not count in `HasUnreportedChildren`. Verify tasks 5.2, 5.5, and 5.6 pass.
- [ ] 6.4 Update `docs/architecture/tool-authorization.md` 3.6 and 4 (a parked prompt survives a restart) and the `netclaw-operations` skill (bump `metadata.version`); run slopwatch and the header check. Verify all pass.

# PR 3: prompt replay

## 7. PR 3 tests first

- [ ] 7.1 Add `ChildPromptReplaySpecs.A_new_subscriber_gets_each_open_child_prompt_once_with_no_child_identity` and `A_rejoin_gets_no_replay`. Verify they fail on PR 2.
- [ ] 7.2 Add `ChannelOutputEngineTests.A_replayed_prompt_with_a_tracked_call_id_is_not_posted_again`, `ChatViewModelTests.A_replayed_prompt_is_not_queued_twice`, and `HeadlessChannelTests.A_replayed_prompt_prints_once`. Verify they fail.
- [ ] 7.3 Add a TUI headless typed-key test: after a restart, a new TUI shows the replayed child prompt, one key approves it, and the child resumes. Verify it fails.

## 8. PR 3 implementation and gates

- [ ] 8.1 Rebuild `ToolInteractionRequest` from the parked request and send it to a new subscriber at its join; add the call ID skip in `ChannelOutputEngine`, `ChatViewModel`, and `HeadlessChannel`. Verify tasks 7.1 to 7.3 pass.
- [ ] 8.2 Update `docs/architecture/tool-authorization.md` 7.5 (skip a replayed call ID). Run `./scripts/smoke/run-smoke.sh light`, slopwatch, and the header check. Verify all pass.

## 9. Finish

- [ ] 9.1 After PR 3 merges, sync this change to the main specs and archive it. Verify `openspec validate --strict` passes on the main specs.
