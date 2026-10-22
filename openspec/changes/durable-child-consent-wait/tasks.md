The work has a spec PR and five code PRs: PR 0, PR 1a, PR 1b, PR 2, and PR 3. Each task names its PR. The design
section "Stack and size" names the base of each PR. Caution: PR 1a must not reach `dev` alone. A separate reviewed
merge PR takes `feature/durable-child-consent-wait` into `dev` only after PR 1b lands.

Each behavior PR starts with failing tests. A test author first adds stub types (`ChildRunPark`, `ChildParkReached`,
`ChildParkRefused`, `ResumeSubAgent`, and an owner handler that keeps today's behavior), so that each new test compiles
and fails on an assertion, not on a compile error. Each test must fail on the base of its PR. No test uses
`Thread.Sleep` or `Task.Delay`. Tests use Akka.TestKit probes, `AwaitAssertAsync`, a fake `TimeProvider`, a fake chat
client, and a recording tool. Each test class that owns a temp folder deletes it (`DisposableTempDir`, `DisposeAsync`).

## 1. Gate before code

- [x] 1.1 (spec PR) Get the owner answer on each decision in design Context, "Owner decisions". Verify the design records each answer as confirmed by the owner, 22 Oct 2026.
- [ ] 1.2 (spec PR) With the OpenSpec skills, remove design D8, tasks 7 to 9, and the child journal, park, purge, and parked-recovery spec deltas from `child-approval-events-and-parent-pause`, then sync and archive it. Verify `openspec validate --strict` passes for both changes.

# PR 0: pure refactors (base `dev`, no behavior change)

## 2. PR 0

- [ ] 2.1 (PR 0) Move the prompt construction of `ParentSessionApprovalBridge.PromptAsync` into one shared builder. Verify the existing parent and child approval tests pass with no change.
- [ ] 2.2 (PR 0) Move "apply one plan entry to a call context" out of `SessionToolExecutionPipeline` (`:538-575`) into one helper (M11). Verify the existing re-drive tests pass with no change.
- [ ] 2.3 (PR 0) Add the export and restore of the loop-guard state to `TurnStateTracker`, with a round-trip unit test (`TurnStateTrackerTests.An_exported_loop_guard_restores_the_same_decisions`). Verify the new test and the existing tests pass.
- [ ] 2.4 (PR 0) Add `ChildConsentPause.Holds(TurnContext authority, bool isChildNotice)` as the one pause predicate, and make `SessionInputFlow.IsHeldByPause` and `WasHeldByPause` call it. Add `ChildConsentPauseTests.The_drain_and_the_recovery_path_hold_the_same_inputs`: one set of inputs (a person, `UntrustedExternal`, `VerifiedAutomation`, `SystemProcess`, a child result notice, an approval notice, a webhook that claims the notice kind) runs through the drain path and the recovery path, and both give the same held set. Verify the test and the existing pause tests pass.
- [ ] 2.5 (PR 0) Run slopwatch and the header check. Verify both pass and the PR changes no behavior test.

# PR 1a: park and resume in memory (base `feature/durable-child-consent-wait`, no format change)

## 3. PR 1a tests first

- [ ] 3.1 (PR 1a) Add the stub types. Verify the solution builds and every PR 1a test below compiles.
- [ ] 3.2 (PR 1a) Add `SubAgentActorParkTests.The_park_holds_the_open_batch_and_no_message_between_results`: a batch with a parked call and a sibling that returns a result and a repeat nudge; assert the transcript ends with the assistant message, the sibling result message is in the open batch, and no nudge is in the transcript. Verify it fails.
- [ ] 3.3 (PR 1a) Add `SubAgentActorParkTests.The_transcript_is_the_serializable_list_and_writes_no_media_again`: a sibling result with an image; assert the park transcript equals the child's `SerializableChatMessage` list and no new media file exists. Verify it fails.
- [ ] 3.4 (PR 1a) Add `SubAgentActorParkTests.A_resume_seeds_one_time_consent_only_for_the_parked_call_id` and `A_refused_parked_call_returns_the_refusal_text_and_never_dispatches`. Verify they fail.
- [ ] 3.5 (PR 1a) Add `SubAgentActorParkTests.The_first_checkpoint_of_a_resumed_child_is_round_N_plus_1` (H1). Verify it fails.
- [ ] 3.6 (PR 1a) Add `SubAgentActorParkTests.The_loop_guard_survives_a_park` (M10) and `A_park_sets_completion_and_stops_the_watchdog` (L2). Verify they fail.
- [ ] 3.7 (PR 1a) Add `ChildParkSpecs.A_child_parks_after_its_siblings_end_and_the_prompt_follows_the_park`, `An_approval_resumes_the_parked_call_once_from_the_park`, `Two_parked_calls_run_only_after_both_answers`, and `A_second_park_uses_new_prompt_numbers_and_a_new_actor_name`. Verify they fail.
- [ ] 3.8 (PR 1a) Add `ChildParkSpecs.A_live_answer_updates_the_park_requests_in_the_persist_callback`: after the answer, the session state holds the answer in `Park.Requests`, not only the workflow table. Verify it fails.
- [ ] 3.9 (PR 1a) Add `ChildParkSpecs.An_answer_during_a_drain_does_not_resume` (N5): the answer is recorded, and no child starts. Verify it fails.
- [ ] 3.10 (PR 1a) Add security specs: `ChildParkSecuritySpecs.A_resumed_child_uses_the_run_authority_while_another_requester_turn_runs`, `A_resumed_call_runs_in_the_recorded_directory_after_the_parent_moved` (H3), `A_narrower_trust_at_resume_narrows_the_tool_set` (M6), `A_deny_rule_added_after_the_park_wins_over_the_answer`, `Another_requester_cannot_answer_a_parked_prompt`, `A_new_call_after_resume_is_not_covered_by_the_old_consent`, `A_changed_parameter_schema_parks_the_call_again`, `A_new_endpoint_with_the_same_name_and_schema_parks_the_call_again` (N3), and `A_description_change_does_not_park_again` (N12). Verify they fail.
- [ ] 3.11 (PR 1a) Add refusal specs with one assertion each on the typed reason (N7): `ChildParkFailureSpecs.A_park_for_a_cancelling_run_ends_cancelled_not_failed`, `A_rejected_park_ends_the_run_with_park_not_recorded`, and `A_park_above_the_cap_ends_the_run_with_park_too_large`, and `A_rejected_two_request_park_replies_and_records_once` (a rejected `PersistAll` of two events gives one reply, one terminal record, and no entry left in `_parksInFlight`). Assert that the child sends no later result that changes the reason. Verify they fail.
- [ ] 3.12 (PR 1a) Add resume failure specs: `A_resume_with_a_degraded_provider_ends_failed`, `A_resume_with_a_deleted_agent_profile_ends_failed`, `A_resume_with_no_spawner_ends_failed` (L5), `A_resume_with_subagents_disabled_ends_failed`, `A_parked_call_to_a_removed_tool_gets_unknown_tool`, and `An_answer_after_the_resume_gets_already_answered`. Assert the reason, the notice text, and no dispatch. Verify they fail.
- [ ] 3.13 (PR 1a) Add control and lifetime specs: `ChildParkControlSpecs.A_cancel_of_a_parked_run_ends_it_cancelled_with_one_write_and_no_actor`, `check_agent_run_reports_parked_with_the_open_count`, `ChildParkSpecs.A_restart_records_a_parked_run_lost_and_withdraws_its_prompt`, and `A_session_with_an_in_memory_park_does_not_passivate` (H5). PR 2 changes the last two on purpose. Verify they fail.

## 4. PR 1a implementation

- [ ] 4.1 (PR 1a) In `SubAgentActor`, keep a `SerializableChatMessage` list next to `_history`; record a parked marker on `ToolApprovalRequiredException` with the fingerprint; build the park with the transcript, the settled result messages, the pre-batch scope, the checkpoint, the loop guard, and the spawn facts; Ask the owner; set completion and stop the watchdog; stop on `ChildParkAccepted` or `ChildParkRefused` with no result. Remove the live wait and `RequestConsentAsync`. Verify tasks 3.2, 3.3, and 3.6 pass.
- [ ] 4.2 (PR 1a) In `LlmSessionActor.Children`, keep `_parksInFlight` with the entry rules of design D3 (add just before the write; remove in the last callback or at the first `ParkNotRecorded`; no entry for a refusal with no write); accept a park only for an owned `Accepted` run; check the size cap and log the size; persist the requests; reply and record the terminal with the typed reason on each refusal, also from `OnPersistRejected` through a self message; keep the park actor-local; number the prompts; emit the prompts. Verify tasks 3.7 and 3.11 pass.
- [ ] 4.3 (PR 1a) Route every child answer to one handler with no live claim; apply the answer to the park in the persist callback; add `TryResumeParkedRun`; add `SubAgentSpawner.PrepareResume` with the spawn rules, the narrower audience, the profile check, and the model log; start the new incarnation with `ResumeSubAgent`. In the child, restore the transcript, the checkpoint seed, the loop guard, and the pre-batch scope; check fingerprints; run the parked calls with the shared helper. Verify tasks 3.4, 3.5, and 3.7 to 3.12 pass.
- [ ] 4.4 (PR 1a) Add the parked cases to `ControlChild` and `ChildStatus`; count an actor-local park in `HasUnreportedChildren`. Verify task 3.13 passes.

## 5. PR 1a docs and gates

- [ ] 5.1 (PR 1a) Update `docs/architecture/tool-authorization.md` 3.5, 3.6, 4, and 9 for park always, the resume path, the scope source, and the fingerprint; rewrite "Parked child run" in `docs/spec/GLOSSARY.md`. Verify the doc links resolve.
- [ ] 5.2 (PR 1a) Add the `child-resume-gate` Stryker target (open-request check, drain check, refusal branch, accept only an owned `Accepted` run, checkpoint seed) to the `child-consent` group with a `TOOLING.md` row and its script. Verify all mutants are killed by PR 1a tests and the group stays under the job timeout.
- [ ] 5.3 (PR 1a) Update the `netclaw-operations` skill (a parked child, state `parked`, the failure reasons) and bump `metadata.version`. Verify `./evals/run-evals.sh` passes.
- [ ] 5.4 (PR 1a) Run slopwatch and the header check. Verify both pass. Open the PR against `feature/durable-child-consent-wait`. Verify the PR base.

# PR 1b: batch effects and managed temporary retry (base `feature/durable-child-consent-wait`, no format change)

## 6. PR 1b tests first

- [ ] 6.1 (PR 1b) Add `SubAgentActorParkTests.A_sibling_set_working_directory_applies_after_the_parked_shell_call` (H2): the shell call runs in the pre-batch directory; after the batch, the project directory and the system message change, and the file activity of both calls is in the next checkpoint. Verify it fails on PR 1a.
- [ ] 6.2 (PR 1b) Add `SubAgentActorParkTests.The_receipt_effects_of_settled_calls_apply_in_live_order` (H2): a sibling exposure request, a `load_tool` result, and a cycle outcome category apply after the parked call, and the media nudge follows all results. Verify it fails.
- [ ] 6.3 (PR 1b) Add `SubAgentActorParkTests.A_managed_temporary_retry_keeps_both_paths_and_its_denial_hint` (M1, N4): an approved retry matches the recorded candidates with both recorded paths; a denied retry gets the denial hint with the recorded directory. Verify it fails.

## 7. PR 1b implementation and gates

- [ ] 7.1 (PR 1b) Add the receipt facts of each settled result to the park, and apply them after the parked calls in live order. Verify tasks 6.1 and 6.2 pass.
- [ ] 7.2 (PR 1b) Add the managed temporary retry target (two paths) and the denial directory to each parked request, and restore them through the shared helper. Do not store the correction state (N4b). Verify task 6.3 passes.
- [ ] 7.3 (PR 1b) Review the mutation scope; add no target, because these rules choose a data source and tasks 6.1 to 6.3 guard them. Record the review in the PR description.
- [ ] 7.4 (PR 1b) Run slopwatch and the header check. Verify both pass.

# Merge PR (base `dev`)

## 8. Feature branch to dev

- [ ] 8.1 (merge PR) After PR 1b lands, open a reviewed PR that merges `feature/durable-child-consent-wait` into `dev`. Verify the full test suite and the `child-consent` Stryker group pass on the combined stack.

# PR 2: durable park, recovery, and channel replay (base `dev`, forward-only)

## 9. PR 2 prerequisite

- [ ] 9.1 (PR 2) Confirm #2587 is merged on `dev`, and rebase PR 2 on it, because the N1 rule edits `TryResumeInterruptedInput` too. Verify the #2587 regression test passes on the PR 2 base.

## 10. PR 2 tests first

- [ ] 10.1 (PR 2) Add `ChildRunParkSerializationTests`: `Parked` (fact 8), `Resumed` (fact 9), the `parked` lifecycle (21), `prompts_issued` (22), `ToolApprovalRequestedProto` fields 25 to 28 with both message fields, round-trip; a record without them reads as before; an unknown lifecycle case fails loud. Verify they fail on the merge PR.
- [ ] 10.2 (PR 2) Change the two PR 1a tests of task 3.13 to the PR 2 rules, and add `ChildParkRecoverySpecs.A_restart_keeps_a_parked_prompt_open_and_a_click_resumes_the_call_once` and `A_wrong_user_after_recovery_is_rejected_from_the_run_record`. Verify they fail.
- [ ] 10.3 (PR 2) Add `ChildParkRecoverySpecs.A_compaction_snapshot_keeps_the_park_and_its_answers` and `A_routine_snapshot_is_not_blocked_by_parked_child_entries`. Verify they fail.
- [ ] 10.4 (PR 2) Add `ChildParkRecoverySpecs.A_crash_after_the_last_answer_resumes_at_recovery`, `A_crash_after_Resumed_records_the_run_lost_with_the_uncertain_text`, `A_rejected_Resumed_ends_the_run_failed_through_a_self_message_and_starts_no_actor` (H4, N9), `An_answer_during_a_drain_in_Ready_resumes_after_recovery`, and `An_answer_during_drain_passivation_gets_the_restart_nack_and_the_prompt_stays_open` (L1). Verify they fail.
- [ ] 10.5 (PR 2) Add `ChildParkRecoverySpecs.The_pause_comes_back_and_a_held_fire_runs_once_after_the_last_answer`, `A_stashed_fire_during_recovery_replaces_the_held_fire_and_the_reminder_runs_once` (M5), `A_damaged_held_input_is_logged_and_recovery_completes` (N8), `A_pending_notice_at_the_crash_gives_one_new_notice_with_the_open_parked_requests`, and `A_restart_withdraws_only_the_prompts_of_runs_that_did_not_park`. Verify they fail.
- [ ] 10.6 (PR 2) Add `ChildParkRecoverySpecs.A_restart_reminder_does_not_run_held_input_while_paused` (N1): a drain with a held fire and a parked open request; after recovery the restart reminder starts no turn for the fire; `A_restart_reminder_still_resumes_an_input_that_the_pause_does_not_hold` (a person input and an `UntrustedExternal` input), and add the restart-resume path to `ChildConsentPauseTests.The_drain_and_the_recovery_path_hold_the_same_inputs`, renamed `The_drain_recovery_and_restart_resume_paths_hold_the_same_inputs`. Verify they fail.
- [ ] 10.7 (PR 2) Add `ChildParkRecoverySpecs.A_parked_run_with_an_open_request_gives_no_restart_reminder`, `A_parked_run_with_all_answers_gives_a_restart_reminder` (M2), `A_session_with_only_parked_children_passivates_and_a_click_wakes_it`, and `A_lost_parked_run_never_says_uncertain` (H5). Verify they fail.
- [ ] 10.8 (PR 2) Add `ChildPromptReplaySpecs.A_prompt_that_was_never_shown_appears_at_the_next_join` (M3), `A_new_subscriber_gets_each_open_parked_prompt_once_with_no_child_identity`, and `A_rejoin_gets_no_replay`. Verify they fail.
- [ ] 10.9 (PR 2) Add `ChannelOutputEngineTests.A_replayed_prompt_with_a_tracked_call_id_is_not_posted_again`, and binding tests for Slack, Mattermost, and Discord (N2): `A_replayed_prompt_with_a_transport_error_logs_and_sends_no_prompt_unavailable` and `A_replayed_prompt_that_the_channel_cannot_render_sends_prompt_unavailable` (for Mattermost, no callback URL), and one permanent-code test for each binding: Slack `is_archived` and `invalid_blocks`, Discord `HttpCode` 403 with `DiscordCode` 50013, Mattermost HTTP 404. Each permanent code sends `prompt_unavailable`. Verify they fail.
- [ ] 10.10 (PR 2) Add `ChildParkSqliteRecoveryTests.A_parked_run_survives_a_SQLite_journal_and_snapshot_restart`. Verify it fails.
- [ ] 10.11 (PR 2) Add `ChildRunParkSnapshotMapperTests.Every_park_field_survives_the_snapshot_mapper`: build a run record in the `parked` state with a non-default value in each park field (the transcript, the settled results with their receipt facts, the pre-batch scope, the checkpoint, the loop guard, the loaded tools, the spawn facts, the requests with their answers, retry targets, denial directories, and fingerprints, and `prompts_issued`); map it to `SessionSnapshotV2Proto` and back; compare the result with the input structurally (deep equivalence over list members, not record `Equals`, which compares lists by reference) (N10). Add `Every_park_field_is_set_in_the_builder`: walk the built park recursively by reflection and assert that every property and every list element is non-default, so a new park field fails the test until the builder and the mapper map it. Verify both fail on the merge PR.

## 11. PR 2 implementation

- [ ] 11.1 (PR 2) Add `ChildRunEvent.Parked`, `ChildRunEvent.Resumed`, `ChildRunLifecycle.Parked`, `PromptsIssued`, the new messages and fields with the numbers of the design, manifests, and mapper entries; apply `ToolApprovalResolved` of a parked run to its park, live and replay. Verify tasks 10.1 and 10.11 pass.
- [ ] 11.2 (PR 2) Persist `Parked` in place of the actor-local park; persist `Resumed` before the spawn; on a `Resumed` rejection record the terminal through a self message. Verify task 10.4 passes.
- [ ] 11.3 (PR 2) Apply the parked requests on replay; add `HydrateParkedChildApprovals`; withdraw only non-parked entries; add the `Parked` cases to `RecoverChildren` and `LostChildResult`; split, log, and restore held inputs inside `RecoveryCompleted` when the pause holds. Verify tasks 10.2 to 10.5 pass.
- [ ] 11.4 (PR 2) In `TryResumeInterruptedInput`, leave out held input while `IsChildConsentPaused`, through `ChildConsentPause.Holds`, and acknowledge with no turn when no input that the pause does not hold remains. Verify task 10.6 passes.
- [ ] 11.5 (PR 2) Change `AllowsSnapshot`, `HasUnreportedChildren`, and the restart-reminder count for parked runs. Verify tasks 10.3, 10.7, and 10.10 pass.
- [ ] 11.6 (PR 2) Replay open parked prompts at a join with `Replayed = true`; skip a tracked call ID in `ChannelOutputEngine`; add the deterministic and transient replay-failure rules to the Slack, Mattermost, and Discord bindings, with the permanent error list of each binding (add `is_archived` to the Slack `PermissionDenied` group; add `MattermostPostFailureClassifier`; add a Discord post classifier over `HttpException`). Verify tasks 10.8 and 10.9 pass.

## 12. PR 2 docs and gates

- [ ] 12.1 (PR 2) Update `docs/architecture/tool-authorization.md` 3.6, 4, and 7.5 (a parked prompt survives a restart; replay, skip, and replay failure). Verify the doc links resolve.
- [ ] 12.2 (PR 2) Add the `child-park-recovery` Stryker target (the `RecoverChildren` decision, the child-entry rule of `AllowsSnapshot`, and the park field assignments of the snapshot mapper in `ToProto` and `FromProto`) to the `child-consent` group with a `TOOLING.md` row. The row names task 10.11 as the main guard of the mapper. Verify all mutants are killed and the group stays under the job timeout.
- [ ] 12.3 (PR 2) Add the downgrade text of the design to the release notes draft, and update the `netclaw-operations` skill (bump `metadata.version`). Verify `./evals/run-evals.sh` passes.
- [ ] 12.4 (PR 2) Run slopwatch and the header check. Verify both pass.

# PR 3: TUI and headless (base `dev`)

## 13. PR 3 tests first

- [ ] 13.1 (PR 3) Add `ChatViewModelTests.A_replayed_prompt_is_not_queued_twice` and `HeadlessChannelTests.A_replayed_prompt_prints_once`. Verify they fail on PR 2.
- [ ] 13.2 (PR 3) Add a TUI headless typed-key test: after a restart, a new TUI shows the replayed child prompt, one key approves it, and the child resumes. Verify it fails.

## 14. PR 3 implementation and gates

- [ ] 14.1 (PR 3) Add the call ID skip in `ChatViewModel` and `HeadlessChannel`. Verify tasks 13.1 and 13.2 pass.
- [ ] 14.2 (PR 3) Run `./scripts/smoke/run-smoke.sh light`, slopwatch, and the header check. Verify all pass.

## 15. Finish

- [ ] 15.1 (after PR 3) Sync this change to the main specs and archive it. Verify `openspec validate --strict` passes on the main specs.
