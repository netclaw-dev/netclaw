## 1. Alert and sink

- [x] 1.1 Add `AlertType.ReminderDeliveryFailed` at the end of the enum.
- [x] 1.2 Give `LlmSessionActor` a required `IOperationalNotificationSink`; update every construction site; verify the solution builds.

## 2. Session behavior

- [x] 2.1 In `HandleDeliveryFailedWhenReady`, read the reminder fire key of the named turn from the recorded turn data; verify with `ReminderTurnDeliveryFailureTests`.
- [x] 2.2 On a final failure of a reminder turn, write the Error log and emit `reminder.delivery.failed`; write no history row; verify with `ReminderTurnDeliveryFailureTests`.
- [x] 2.3 Keep the alert off for a turn with no reminder source and for a retryable failure that a retry recovers; verify with `ReminderTurnDeliveryFailureTests`.
- [x] 2.4 Raise the alert when the retry budget ends on a reminder turn; verify with `ReminderTurnDeliveryFailureTests`.
- [x] 2.5 Find the reminder after the session recovers from its journal; verify with `ReminderTurnDeliveryFailureTests`.

## 3. Docs and proof

- [x] 3.1 Update `netclaw-operations` `references/scheduling.md` and bump `metadata.version`.
- [x] 3.2 Run `MattermostReminderIntegrationTests` with `NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1`.
- [x] 3.3 Run the Actors suite, slopwatch, and the header check.

## 4. Review round 1: turn number (B1)

Tests exist and fail until the product code changes. Do not edit the tests.

- [x] 4.1 Make Mattermost and Discord hold the first failed post of a turn and send `DeliveryFailed` from the turn-completion hook with `completed.TurnNumber`. Send no report for a turn with a posted output. Report a failed empty-turn fallback post with the turn that it answers. Discard the held failure on a pipeline reinitialize. Verify with `SessionBindingContractTests` (`Failed_post_reports_the_turn_it_belongs_to_once_per_turn`, `Failed_post_in_a_turn_with_a_delivered_post_sends_no_DeliveryFailed`, `Failed_empty_turn_fallback_post_reports_the_turn_it_answers`) for all three channels.
- [x] 4.2 Make Slack report a failed empty-turn fallback post. Slack ignores the result today.
- [x] 4.3 Remove `ChannelOutputEngine.LastCompletedTurnNumber` if no caller remains, and fix the engine remarks about the hook.
- [x] 4.4 Verify with `MattermostReminderIntegrationTests` (`failed.TurnNumber` is turn 1).

## 5. Review round 1: report once (S1) and unknown turn (S3)

- [x] 5.1 Give `DeliveryRetryHandler` `ReminderOf` with three answers (`ReminderLookup` with `ReminderLookupKind.UnknownTurn`, `NoReminder`, `Reminder`) and `TryMarkReported(TurnNumber)`. Verify with `DeliveryRetryHandlerReminderTests`.
- [x] 5.2 Route both `DeliveryFailed` handlers through `ReportReminderPostFailure`. Write the Warning `cannot tell whether a reminder started turn {Turn}` for an unknown turn. Write a Warning for a fire key that does not parse. Verify with `ReminderTurnDeliveryFailureTests`.

## 6. Review round 1: routed-skill turns (S2, owner decision)

Tests exist and fail until the product code changes. Do not edit the tests.

- [x] 6.1 Add `bool RoutedSkill` to `TurnRecorded` (default `false`), `bool routed_skill = 8;` (renumbered to 10 in round 3, see 10.1) to `TurnRecordedProto`, and the mapper. The live routed-skill path writes `true`. Verify with `SerializationRoundTripTests` (`TurnRecorded_routed_skill_mark_round_trips_as_a_plain_proto_bool`, `TurnRecorded_from_before_the_routed_mark_reads_as_not_routed`).
- [x] 6.2 Change `DeliveryRetryHandler.RecordTurn` to `RecordTurn(TurnNumber, ReminderId?, bool routedSkill)` (no default value) and add `IsRoutedSkill(TurnNumber)`. Verify with `DeliveryRetryHandlerReminderTests`.
- [x] 6.3 Add one helper, `RecordEligibleTurn(TurnRecorded)`, and call it from the live text turn, the live routed-skill turn, and `Recover<TurnRecorded>`. It passes `evt.RoutedSkill`.
- [x] 6.4 In `HandleDeliveryFailedWhenReady`, skip the retry branch for a routed-skill turn: write a Warning, call `ReportReminderPostFailure`, and mark the turn not eligible. Make no model call. Verify with `RoutedSkillReminderDeliveryTests` and `A_recovered_ordinary_reminder_turn_still_retries_a_retryable_failure_on_the_main_model`.

## 7. Review round 1: snapshot (S3)

- [x] 7.1 Move `MaybeSnapshot()` after `RecordEligibleTurn` in both live paths. Verify with `ReminderIntervalSnapshotRecoveryTests`.
- [x] 7.2 Add `SessionSnapshot.EligibleDeliveryReminderId` and `optional string eligible_delivery_reminder_id = 12;` (renumbered to 20 in round 3, see 10.1) in `netclaw_messages.proto`, with the mapper. `BuildSnapshot` writes it only for an eligible, recorded, unreported reminder turn. `SnapshotOffer` calls `RecordTurn` when the reminder field has a value or the routed mark is true. Add `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` and `bool eligible_delivery_turn_routed_skill = 13;` (now `optional bool`, field 21, see 10.1); `BuildSnapshot` writes the mark of the eligible turn and `SnapshotOffer` restores it, also for a routed turn that a person started. Verify with `SerializationRoundTripTests` and the passivation tests in `ReminderTurnDeliveryFailureTests` and `RoutedSkillReminderDeliveryTests`.

## 8. Review round 1: mutation target

- [x] 8.1 Add a narrow Stryker target for `DeliveryRetryHandler.ReminderOf` and `TryMarkReported`. `DeliveryRetryHandlerReminderTests` is already linked into `Netclaw.Actors.MutationTests`. Add the script, the `mutation-gates` entry, the expected mutant count, and the `TOOLING.md` row.

## 9. Review round 1: docs

- [x] 9.1 Update `netclaw-operations` `references/scheduling.md`. Say that the alert reaches only the configured notification webhooks, and that the daemon log has the Error line when no webhook exists. Bump `metadata.version` above the highest open-PR value (PR #2528 uses 2.111.0).
- [x] 9.2 Add `Directory.Packages.props`, `global.json`, `src/Netclaw.Actors/Channels/**`, and `src/Netclaw.Actors/Protocol/**` to the path filter of `.github/workflows/mattermost_integration.yml`.
- [x] 9.3 Fix the spec nits: blank line before each new requirement heading, the `Channel`-only evidence sentence in `netclaw-scheduling`, and the name of the job ledger in `background-job-execution`.

## 10. Review round 3

Tests exist and fail until the product code changes. Do not edit the tests.

- [x] 10.1 Renumber the proto fields: `TurnRecordedProto.routed_skill = 10`, `SessionSnapshotProto.eligible_delivery_reminder_id = 20`, `SessionSnapshotProto.eligible_delivery_turn_routed_skill = 21`. Make the routed mark of the snapshot `optional bool`, and make `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` a `bool?` (null when absent). The mapper writes the value when it is not null. Verify with `SerializationRoundTripTests` (`The_delivery_fields_use_numbers_that_no_other_branch_uses` and the wire tests next to it).
- [x] 10.2 `BuildSnapshot` writes a routed mark (true or false) in every snapshot. `SnapshotOffer` calls `RecordTurn(eligibleTurn, reminderId, routed)` whenever the snapshot has an eligible turn and a routed mark, also for a null reminder and a false mark. It records nothing when the mark is null. Verify with `ReminderTurnDeliveryFailureTests` (`A_person_turn_that_the_session_restored_from_its_snapshot_returns_quietly`, `A_new_format_snapshot_of_a_turn_with_no_reminder_returns_quietly`, `An_old_format_snapshot_still_gets_the_unknown_turn_warning`, `A_reported_turn_does_not_alert_again_after_the_session_passivated`).
- [x] 10.3 Add `Command<DeliveryFailed>` to `Compacting()`. Reuse the rule of the `Processing` handler, except that `Compacting` adds no nudge (round 3, S1: compaction rebuilds the history and discards a nudge). Verify with `ReminderCompactionDeliveryTests`.
- [x] 10.4 In the restart-drain branches of `Processing` and `Passivating`, call `ReportReminderPostFailure(msg)` before the return. Add no nudge and make no retry. Verify with `ReminderRestartDrainDeliveryTests`.
- [x] 10.5 In the Discord and Mattermost bindings, hold a failed post only from the output wrappers that the engine calls (`postTextAsync`, `uploadFileAsync`). The notifier of `SafeTransportCall` for all other posts only logs. Verify with `Failed_post_that_is_not_turn_output_does_not_replace_the_failure_of_the_next_turn` in the Discord and Mattermost contract tests.
- [x] 10.6 Run the Actors `Sessions`, `Channels`, and `Protocol` test folders, the Mattermost integration project (`NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1`), `dotnet slopwatch analyze`, and the header check.

## 11. Child-delivery turns keep the reminder link (rebase onto the coordination stack)

Tests exist and fail until the product code changes. Do not edit the tests.

- [x] 11.1 Add `BackgroundChildRun.SourceReminderId` (required `ReminderId?`) and `optional string source_reminder_id = 20` to `BackgroundChildRunV2Proto`, with the mapper. A draft v1 or deployed v1 record reads as null. Verify with `SerializationRoundTripTests` (`The_child_run_reminder_link_uses_field_20_with_explicit_presence`, `ChildRunAccepted_keeps_the_reminder_link_of_the_turn_that_started_the_child`, `ChildRunAccepted_of_a_person_turn_writes_no_reminder_link`, `A_child_run_record_from_before_the_link_reads_as_no_link_and_a_hand_built_field_20_reads_as_the_link`, `SessionSnapshot_child_run_keeps_the_reminder_link`).
- [x] 11.2 Add `RunningTurnReminder(deliveredChild)` to `LlmSessionActor`. `AcceptChild` writes its value into the child run. `HandleTextResponse` writes it into `TurnRecorded.SourceReminderId` and sets `TurnRecorded.RoutedSkill` for a routed-skill child. Do not change `TurnCompleted`. Verify with `ChildDeliveryReminderLinkTests`, `ChildDeliveryReminderLinkSnapshotTests`, and the child-result tests in `RoutedSkillReminderDeliveryTests`.
- [x] 11.3 Add `DeliveryRetryHandler.RetriedTurnReminder`: `RecordRetry` starts it, and `RecordTurn` and `Clear` end it. Verify with `DeliveryRetryHandlerReminderTests` and `A_spent_retry_budget_on_the_child_result_of_a_reminder_turn_raises_one_alert`.
- [x] 11.4 Widen `./scripts/run-reminder-report-once-mutations.sh` to the retry link, and update the `TOOLING.md` row and section.
- [x] 11.5 Fix the `netclaw-operations` `references/scheduling.md` text: the alert covers the child result of a reminder, for a routed skill and for an async sub-agent.
- [x] 11.6 Run `Netclaw.Actors.Tests`, `Netclaw.Actors.MutationTests`, `Netclaw.Daemon.Tests`, `Netclaw.Cli.Tests`, the report-once mutation gate, `dotnet slopwatch analyze`, and the header check.
