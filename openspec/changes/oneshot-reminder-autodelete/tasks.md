## 1. Tombstone store and protocol

- [ ] 1.1 Add `ReminderTombstone` (id, title, audience, fireAt, outcome, endedAtMs, lastError) and the `Cancelled` and `Expired` values of `ReminderTerminalOutcome`; verify with `ReminderEnumContractTests` that the new values serialize as strings and that the existing values keep their names.
- [ ] 1.2 Add `SaveTombstone`, `GetTombstone`, `ListTombstones`, and `DeleteTombstone` to `ReminderDefinitionStore` under `{RemindersDirectory}/ended/`, with the existing lock, path containment check, and temp-file rename; verify in `ReminderDefinitionStoreTests` that `List()` never returns a tombstone, that an id with `../` stays inside `ended/`, and that a corrupt tombstone returns null, logs an error, and stays on disk.
- [ ] 1.3 Add `ReminderEndedInfo` and the additive fields `GetReminderResponse.Ended`, `ReminderStatusResponse.Ended`, `ReminderCancelledResponse.AlreadyEnded`, and `ReminderCancelledResponse.ErrorMessage`; verify that the solution builds and that the existing protocol tests pass.

## 2. One end path in the manager

- [ ] 2.1 Add `EndOneShotAsync` in `ReminderManagerActor` with the D3 order (tombstone, envelope action, schedule cancel, actor-local cleanup, definition delete) and the stop on a tombstone write failure; verify with `Oneshot_tombstone_write_failure_does_not_ack_or_delete` (a store fake that throws).
- [ ] 2.2 Use it on success, denied run, threshold failure, and nack `Failed`/`Expired`, and delete `RetainCompletedOneShotAsync`; verify with `Successful_oneshot_is_deleted_and_leaves_a_completed_tombstone_with_history`, `Denied_oneshot_run_ends_failed_with_denied_history`, `Oneshot_failure_below_threshold_keeps_the_definition_enabled`, `Oneshot_fifth_failure_ends_failed_and_cancels_the_schedule`, and `Oneshot_ends_failed_when_the_nack_replies_expired`.
- [ ] 2.3 Add the D5 check (definition fire time equals the envelope due time, in milliseconds) before each end in settlement; verify with `Rearm_during_the_run_keeps_the_new_definition` (positive), `Settled_occurrence_that_matches_the_definition_ends_it` (negative control), and `Cancel_during_the_run_keeps_the_cancelled_tombstone`.
- [ ] 2.4 End an expired one-shot at fire with `Expired` (tombstone before the ack) and keep the recurring expiry path; verify by changing `Expired_reminder_disabled_on_fire_without_executing` into `Expired_oneshot_fire_ends_expired_without_executing` plus a recurring variant that stays disabled.
- [ ] 2.5 In the fire handler, finish a definition that has a matching tombstone without a run, and log at Info for a fire after the end; verify with `Fire_with_a_matching_tombstone_does_not_run` (the pipeline probe gets no message, the envelope is acknowledged, the definition is deleted).

## 3. Cancel, re-arm, and reads

- [ ] 3.1 Make `HandleCancelAsync` end a one-shot as `Cancelled` and reply `AlreadyEnded` for a tombstone in the caller's audience; verify with `Cancel_oneshot_deletes_it_and_writes_a_cancelled_tombstone`, `Cancel_recurring_disables_and_keeps_it` (rename of `Cancel_existing_reminder_disables_and_preserves_definition`), `Cancel_of_an_ended_oneshot_reports_already_ended`, and `Cancel_of_an_ended_oneshot_outside_the_audience_is_not_found`.
- [ ] 3.2 Apply the D6 re-arm order and the tombstone audience gate in `HandleSaveAsync`, and remove the `Completed`-only `CreateOnly` exception; verify with `Rearm_after_the_end_discards_the_tombstone_and_history` (replaces `Completed_oneshot_id_can_be_created_again_with_a_fresh_history_but_a_failed_one_cannot`), `Rearm_blocked_by_a_tombstone_outside_the_audience`, `Rearm_with_a_failed_schedule_keeps_the_tombstone_and_history`, and `Restart_recovery_create_only_reuses_an_ended_id`.
- [ ] 3.3 Serve history, status, get, enable, disable, and permanent delete from the tombstone when no definition exists; verify with `History_and_status_of_an_ended_oneshot_use_the_tombstone_audience` (in scope and out of scope), `Permanent_delete_removes_history_then_tombstone`, and `Get_of_an_ended_oneshot_returns_the_ended_info`.

## 4. Sweep, reconciliation, and upgrade

- [ ] 4.1 Replace `PruneTerminalRemindersAsync` with the D8 sweep (finish matching, drop stale, end leftovers, purge after `TerminalRetention`) on the existing 12-hour timer; verify with `FakeTimeProvider`: `Tombstone_and_history_purged_after_retention` advances the time 12 days plus 1 minute and fires the timer, and `Tombstone_kept_inside_retention` advances 12 days minus 1 minute.
- [ ] 4.2 Use `EndOneShotAsync` in the startup reconciliation for the durable occurrence statuses, and add `ended_oneshots` and `purged_tombstones` to the log line; verify by changing `Reconcile_retains_a_delivered_oneshot_whose_completion_was_not_saved` into `Reconcile_ends_a_delivered_oneshot_whose_tombstone_was_not_written` and adding a `Failed` status case.
- [ ] 4.3 Add the upgrade fixture test `Upgrade_reconcile_ends_leftover_oneshots_and_keeps_live_reminders` with old `Completed` (20 days), recent `Failed` (2 days), disabled without outcome (fire time 30 days ago), paused one-shot with a future fire time, disabled interval, `Failed` cron, and enabled future one-shot; verify the D8 outcome for each, and replace `Only_completed_oneshots_past_the_retention_period_are_pruned_with_their_history`, `Reconcile_prunes_stale_completed_oneshots_but_never_old_failed_reminders`, and `Reconcile_retains_recent_terminal_oneshots_and_ambiguous_oneshots`.
- [ ] 4.4 Add `Stale_tombstone_does_not_block_a_newer_definition`; verify that the definition stays scheduled, the stale tombstone is deleted, and a warning names the id.

## 5. Crash and restart proof

Each case writes the intermediate files directly, then starts a new manager on the same stores and the same in-memory Akka.Reminders storage, as `Manager_restart_preserves_definition_and_occurrence_state` does. No test uses `Task.Delay` or `Thread.Sleep`; the tests use `FakeTimeProvider` and `AwaitAssertAsync`.

- [ ] 5.1 Tombstone written, envelope not acknowledged, definition present: verify that the redelivery runs nothing and that the manager acknowledges it and deletes the definition.
- [ ] 5.2 Envelope acknowledged, no tombstone: verify that the reconciliation ends the one-shot as `Completed` from the `Delivered` status.
- [ ] 5.3 Cancel tombstone written, schedule and definition present: verify that the reconciliation cancels the schedule and deletes the definition, and that a fire before the reconciliation runs nothing.
- [ ] 5.4 Old-version state (disabled `Failed` definition from a threshold failure before the nack): verify that the redelivery is acknowledged without a run and that the sweep ends it as `Failed`.
- [ ] 5.5 Re-arm stopped after the schedule and before the definition save: verify that the reconciliation cancels the orphan schedule and that a fire before that runs nothing.

## 6. Mutation proof

- [ ] 6.1 Add the tombstone match (D4) and the settled-occurrence check (D5) as one narrow target in `./scripts/run-reminder-execution-mutations.sh` and the `TOOLING.md` target table; verify that every mutant dies and that the CI cost stays inside the documented budget.

## 7. Daemon and CLI surfaces

- [ ] 7.1 Update the REST cancel message, `GET /{id}` 404 body, `/status` ended fields, and `/history` for an ended one-shot; verify with `ReminderEndpointAuthorizationTests` cases for an ended one-shot in scope and out of scope.
- [ ] 7.2 Update `netclaw reminder show`, `status`, `cancel`, and the help text; verify with `ReminderCliDaemonContractTests` that `show` exits with code 1 and prints the outcome, the end time, and the purge time, and that `status` prints the ended line.
- [ ] 7.3 Update the texts of `cancel_reminder` (description and reply), `set_reminder` (one-shot reply), and `run_reminder` (ended error); verify with `SetReminderToolTests` and a `CancelReminderTool` reply test for one-shot and recurring.

## 8. Docs and skills

- [ ] 8.1 Remove the "(planned in ...)" note from the code anchors of "Reminder tombstone" in `docs/spec/GLOSSARY.md` (this change added the term); verify that the `#reminder-tombstone` link in the spec delta resolves.
- [ ] 8.2 Update `feeds/skills/.system/files/netclaw-operations/references/scheduling.md` with the D10 text and bump `metadata.version` in `netclaw-operations/SKILL.md`; verify that no text still says that `cancel_reminder` keeps a one-shot or that a `Failed` one-shot keeps its id.
- [ ] 8.3 Update `feeds/skills/.system/files/run-reminder/SKILL.md` with the D10 sentence and bump its `metadata.version`.
- [ ] 8.4 Run the eval suite (`./evals/run-evals.sh`), because skill content changed; verify that it passes.

## 9. Final checks

- [ ] 9.1 Run the Actors, Daemon, and Cli test suites; verify that they pass and that the test temp root reports no leaks.
- [ ] 9.2 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`; verify no new violations.
- [ ] 9.3 Rebase on the coordination stack if it merged first, and resolve the `netclaw-operations` version bump; verify that the skill version is higher than both parents.
- [ ] 9.4 Run `/opsx-verify` against this change, then `/opsx-sync`; verify that the main specs match the delta.
