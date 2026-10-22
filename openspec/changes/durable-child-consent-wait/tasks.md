The stack has three code PRs on `feature/durable-child-consent-wait`. Each PR starts with failing tests. A test author
first adds stub types (`ChildRunPark`, `ChildParkReached`, `ResumeSubAgent`, and an owner handler that keeps today's
behavior), so that each new test compiles and fails on an assertion, not on a compile error. Each test must fail on
the base of its PR. No test uses `Thread.Sleep` or `Task.Delay`. Tests use Akka.TestKit probes, `AwaitAssertAsync`,
a fake `TimeProvider`, a fake chat client, and a recording tool. Each test class that owns a temp folder deletes it
(`DisposableTempDir`, `DisposeAsync`).

## 1. Gate before code

- [ ] 1.1 Get the owner answer on each default in design Context, "Owner decisions (pending owner confirmation)". Verify the design records each answer and drops the "pending" mark.
- [ ] 1.2 With the OpenSpec skills, remove design D8, tasks 7 to 9, and the child journal, park, purge, and parked-recovery spec deltas from `child-approval-events-and-parent-pause`, then sync and archive it. Verify `openspec validate --strict` passes for both changes.

# PR 1: park and resume in memory (no format change)

## 2. PR 1 tests first

- [ ] 2.1 Add the stub types. Verify the solution builds and every test below compiles.
- [ ] 2.2 Add `SubAgentActorParkTests.The_park_holds_the_open_batch_and_no_message_between_results`: a batch with a parked call and a sibling that returns media and a repeat nudge; assert the transcript ends with the assistant message, the sibling result and its receipt facts are in the open batch, and no nudge is in the transcript. Verify it fails.
- [ ] 2.3 Add `SubAgentActorParkTests.The_transcript_is_the_serializable_list_and_writes_no_media_again`: a sibling result with an image; assert the park transcript equals the child's `SerializableChatMessage` list and no new media file exists. Verify it fails.
- [ ] 2.4 Add `SubAgentActorParkTests.A_resume_seeds_one_time_consent_only_for_the_parked_call_id` and `A_refused_parked_call_returns_the_refusal_text_and_never_dispatches`: the recording tool sees one dispatch for the approved call ID and none for the refused one. Verify they fail.
- [ ] 2.5 Add `SubAgentActorParkTests.The_first_checkpoint_of_a_resumed_child_is_round_N_plus_1` (H1): park after round N; resume; assert the owner receives round N+1 and the session does not throw. Verify it fails.
- [ ] 2.6 Add `SubAgentActorParkTests.A_sibling_set_working_directory_applies_after_the_parked_shell_call` (H2, H3): the shell call runs in the pre-batch directory; after the batch, the project directory and the system message change, and the file activity of both calls is in the next checkpoint. Verify it fails.
- [ ] 2.7 Add `SubAgentActorParkTests.The_loop_guard_survives_a_park` (M10): a refused repeat before the park and one after it give the escalated nudge as in a live run. Verify it fails.
- [ ] 2.8 Add `SubAgentActorParkTests.A_managed_temporary_retry_keeps_its_target_and_its_denial_hint` (M1): an approved retry matches the recorded candidates; a denied retry gets the denial hint with the recorded directory. Verify it fails.
- [ ] 2.9 Add `SubAgentActorParkTests.A_park_sets_completion_and_stops_the_watchdog` (L2): assert no `ActorStopped` result after the stop and no watchdog expiry. Verify it fails.
- [ ] 2.10 Add `ChildParkSpecs.A_child_parks_after_its_siblings_end_and_the_prompt_follows_the_park`: the journal probe sees the requests before the `ToolInteractionRequest` output; the sibling ran once; the child terminated; no lost record. Verify it fails.
- [ ] 2.11 Add `ChildParkSpecs.An_approval_resumes_the_parked_call_once_from_the_park`, `Two_parked_calls_run_only_after_both_answers`, and `A_second_park_uses_new_prompt_numbers_and_a_new_actor_name`. Verify they fail.
- [ ] 2.12 Add security specs: `ChildParkSecuritySpecs.A_resumed_child_uses_the_run_authority_while_another_requester_turn_runs`, `A_resumed_call_runs_in_the_recorded_directory_after_the_parent_moved`, `A_narrower_trust_at_resume_narrows_the_tool_set` (M6), `A_deny_rule_added_after_the_park_wins_over_the_answer`, `Another_requester_cannot_answer_a_parked_prompt`, `A_new_call_after_resume_is_not_covered_by_the_old_consent`, and `A_changed_tool_fingerprint_parks_the_call_again` (M7). Verify they fail.
- [ ] 2.13 Add failure specs: `ChildParkFailureSpecs.A_rejected_park_ends_the_run_failed_with_park_not_recorded` (H4), `A_park_above_the_cap_ends_the_run_with_park_too_large` (M8), `A_resume_with_a_degraded_provider_ends_failed`, `A_resume_with_a_deleted_agent_profile_ends_failed`, `A_resume_with_no_spawner_ends_failed` (L5), `A_resume_with_subagents_disabled_ends_failed`, `A_parked_call_to_a_removed_tool_gets_unknown_tool`, and `An_answer_after_the_resume_gets_already_answered`. Assert the reason, the notice text, and no dispatch. Verify they fail.
- [ ] 2.14 Add control specs: `ChildParkControlSpecs.A_cancel_of_a_parked_run_ends_it_cancelled_with_one_write_and_no_actor`, `A_park_for_a_cancelling_run_is_refused`, and `check_agent_run_reports_parked_with_the_open_count`. Verify they fail.
- [ ] 2.15 Add PR 1 lifetime specs: `ChildParkSpecs.A_restart_records_a_parked_run_lost_and_withdraws_its_prompt` and `A_session_with_an_in_memory_park_does_not_passivate` (H5). PR 2 changes both on purpose. Verify they fail.

## 3. PR 1 implementation

- [ ] 3.1 Move the prompt construction of `ParentSessionApprovalBridge.PromptAsync` into one shared builder; remove `RequestConsentAsync` and the child `Interlocked` numbering. Verify the existing parent approval tests pass.
- [ ] 3.2 Move "apply one plan entry to a call context" out of `SessionToolExecutionPipeline` (`:538-575`) into one helper for the pipeline and the child (M11). Verify the existing re-drive tests pass.
- [ ] 3.3 Add the export and restore of the loop-guard state to `TurnStateTracker`. Verify task 2.7 passes.
- [ ] 3.4 In `SubAgentActor`, keep a `SerializableChatMessage` list next to `_history`; record a parked marker on `ToolApprovalRequiredException` with the retry target and the fingerprint; build the park with the receipt facts, the pre-batch scope, the checkpoint, the loop guard, the managed temporary state, and the spawn facts; Ask the owner; set completion and stop the watchdog; stop after the ack. Remove the live wait. Verify tasks 2.2, 2.3, 2.8, and 2.9 pass.
- [ ] 3.5 In `LlmSessionActor.Children`, accept a park only for an owned `Accepted` run; check the size cap and log the size; persist the requests; on a rejection or failure reply `Status.Failure`; keep the park actor-local; number the prompts; emit the prompts. Verify tasks 2.10 and 2.13 (park part) pass.
- [ ] 3.6 Route every child answer to one handler with no live claim; add `TryResumeParkedRun`; add `SubAgentSpawner.PrepareResume` with the spawn rules, the narrower audience, the profile check, and the model log; start the new incarnation with `ResumeSubAgent`. In the child, restore the state, check fingerprints, run the parked calls with the shared helper, and complete the batch in live order. Verify tasks 2.4 to 2.6, 2.11 to 2.13 pass.
- [ ] 3.7 Add the parked cases to `ControlChild` and `ChildStatus`; count an actor-local park in `HasUnreportedChildren`. Verify tasks 2.14 and 2.15 pass.

## 4. PR 1 docs and gates

- [ ] 4.1 Update `docs/architecture/tool-authorization.md` 3.5, 3.6, 4, and 9 for park always and the resume path. Verify the doc links resolve.
- [ ] 4.2 Update `docs/spec/GLOSSARY.md`: rewrite "Parked child run". Verify no doc links to a removed term.
- [ ] 4.3 Add the `child-resume-gate` Stryker target (open-request check, drain check, refusal branch, accept only an owned `Accepted` run, checkpoint seed) to the `child-consent` group with a `TOOLING.md` row and its script. Verify all mutants are killed and the group stays under the job timeout.
- [ ] 4.4 Update the `netclaw-operations` skill (a parked child, state `parked`, the failure reasons) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [ ] 4.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify both pass.

# PR 2: durable park, recovery, and channel replay (forward-only)

## 5. PR 2 prerequisite

- [ ] 5.1 Confirm #2587 is merged on `dev`. Verify its regression test passes on the PR 2 base.

## 6. PR 2 tests first

- [ ] 6.1 Add `ChildRunParkSerializationTests`: `Parked` (fact 8), `Resumed` (fact 9), the `parked` lifecycle (21), `prompts_issued` (22), and `ToolApprovalRequestedProto` fields 25 to 28 round-trip; a record without them reads as before; an unknown lifecycle case fails loud. Verify they fail on PR 1.
- [ ] 6.2 Change the two tests of task 2.15 to the PR 2 rules, and add `ChildParkRecoverySpecs.A_restart_keeps_a_parked_prompt_open_and_a_click_resumes_the_call_once` and `A_wrong_user_after_recovery_is_rejected_from_the_run_record`. Verify they fail.
- [ ] 6.3 Add `ChildParkRecoverySpecs.A_compaction_snapshot_keeps_the_park_and_its_answers` and `A_routine_snapshot_is_not_blocked_by_parked_child_entries`. Verify they fail.
- [ ] 6.4 Add `ChildParkRecoverySpecs.A_crash_after_the_last_answer_resumes_at_recovery`, `A_crash_after_Resumed_records_the_run_lost_with_the_uncertain_text`, `A_rejected_Resumed_ends_the_run_failed_and_starts_no_actor` (H4), `An_answer_during_a_drain_in_Ready_resumes_after_recovery`, and `An_answer_during_drain_passivation_gets_the_restart_nack_and_the_prompt_stays_open` (L1). Verify they fail.
- [ ] 6.5 Add `ChildParkRecoverySpecs.The_pause_comes_back_and_a_held_fire_runs_once_after_the_last_answer`, `A_stashed_fire_during_recovery_replaces_the_held_fire_and_the_reminder_runs_once` (M5), `A_pending_notice_at_the_crash_gives_one_new_notice_with_the_open_parked_requests`, and `A_restart_withdraws_only_the_prompts_of_runs_that_did_not_park`. Verify they fail.
- [ ] 6.6 Add `ChildParkRecoverySpecs.A_parked_run_with_an_open_request_gives_no_restart_reminder` and `A_parked_run_with_all_answers_gives_a_restart_reminder` (M2), `A_session_with_only_parked_children_passivates_and_a_click_wakes_it`, and `A_lost_parked_run_never_says_uncertain` (H5). Verify they fail.
- [ ] 6.7 Add `ChildPromptReplaySpecs.A_prompt_that_was_never_shown_appears_at_the_next_join` (M3), `A_new_subscriber_gets_each_open_parked_prompt_once_with_no_child_identity`, and `A_rejoin_gets_no_replay`. Verify they fail.
- [ ] 6.8 Add `ChannelOutputEngineTests.A_replayed_prompt_with_a_tracked_call_id_is_not_posted_again` and binding tests for Slack, Mattermost, and Discord: `A_replayed_prompt_that_fails_to_post_logs_and_sends_no_prompt_unavailable` (M4). Verify they fail.
- [ ] 6.9 Add `ChildParkSqliteRecoveryTests.A_parked_run_survives_a_SQLite_journal_and_snapshot_restart`. Verify it fails.

## 7. PR 2 implementation

- [ ] 7.1 Add `ChildRunEvent.Parked`, `ChildRunEvent.Resumed`, `ChildRunLifecycle.Parked`, `PromptsIssued`, the new messages and fields with the numbers of the design, manifests, and mapper entries; apply `ToolApprovalResolved` of a parked run to its park. Verify task 6.1 passes.
- [ ] 7.2 Persist `Parked` in place of the actor-local park; persist `Resumed` before the spawn; on a `Resumed` rejection end the run with `resume_not_recorded`. Verify tasks 6.4 (write part) pass.
- [ ] 7.3 Apply the parked requests on replay; add `HydrateParkedChildApprovals`; withdraw only non-parked entries; add the `Parked` cases to `RecoverChildren` and `LostChildResult`; restore held inputs inside `RecoveryCompleted` when the pause holds. Verify tasks 6.2 to 6.5 pass.
- [ ] 7.4 Change `AllowsSnapshot`, `HasUnreportedChildren`, and the restart-reminder count for parked runs. Verify tasks 6.3, 6.6, and 6.9 pass.
- [ ] 7.5 Replay open parked prompts at a join with `Replayed = true`; skip a tracked call ID in `ChannelOutputEngine`; add the replay-failure rule to the Slack, Mattermost, and Discord bindings. Verify tasks 6.7 and 6.8 pass.

## 8. PR 2 docs and gates

- [ ] 8.1 Update `docs/architecture/tool-authorization.md` 3.6, 4, and 7.5 (a parked prompt survives a restart; replay and skip). Verify the doc links resolve.
- [ ] 8.2 Add the `child-park-recovery` Stryker target (the `RecoverChildren` decision and the child-entry rule of `AllowsSnapshot`) to the `child-consent` group with a `TOOLING.md` row. Verify all mutants are killed and the group stays under the job timeout.
- [ ] 8.3 Add the downgrade text of the design to the release notes draft, and update the `netclaw-operations` skill (bump `metadata.version`). Verify `./evals/run-evals.sh` passes.
- [ ] 8.4 Run slopwatch and the header check. Verify both pass.

# PR 3: TUI and headless

## 9. PR 3 tests first

- [ ] 9.1 Add `ChatViewModelTests.A_replayed_prompt_is_not_queued_twice` and `HeadlessChannelTests.A_replayed_prompt_prints_once`. Verify they fail on PR 2.
- [ ] 9.2 Add a TUI headless typed-key test: after a restart, a new TUI shows the replayed child prompt, one key approves it, and the child resumes. Verify it fails.

## 10. PR 3 implementation and gates

- [ ] 10.1 Add the call ID skip in `ChatViewModel` and `HeadlessChannel`. Verify tasks 9.1 and 9.2 pass.
- [ ] 10.2 Run `./scripts/smoke/run-smoke.sh light`, slopwatch, and the header check. Verify all pass.

## 11. Finish

- [ ] 11.1 After PR 3 merges, sync this change to the main specs and archive it. Verify `openspec validate --strict` passes on the main specs.
