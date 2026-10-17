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

- [ ] 4.1 Make Mattermost and Discord hold the first failed post of a turn and send `DeliveryFailed` from the turn-completion hook with `completed.TurnNumber`. Send no report for a turn with a posted output. Report a failed empty-turn fallback post with the turn that it answers. Discard the held failure on a pipeline reinitialize. Verify with `SessionBindingContractTests` (`Failed_post_reports_the_turn_it_belongs_to_once_per_turn`, `Failed_post_in_a_turn_with_a_delivered_post_sends_no_DeliveryFailed`, `Failed_empty_turn_fallback_post_reports_the_turn_it_answers`) for all three channels.
- [ ] 4.2 Make Slack report a failed empty-turn fallback post. Slack ignores the result today.
- [ ] 4.3 Remove `ChannelOutputEngine.LastCompletedTurnNumber` if no caller remains, and fix the engine remarks about the hook.
- [ ] 4.4 Verify with `MattermostReminderIntegrationTests` (`failed.TurnNumber` is turn 1).

## 5. Review round 1: report once (S1) and unknown turn (S3)

- [ ] 5.1 Give `DeliveryRetryHandler` `ReminderOf` with three answers (`ReminderLookup` with `ReminderLookupKind.UnknownTurn`, `NoReminder`, `Reminder`) and `TryMarkReported(TurnNumber)`. Verify with `DeliveryRetryHandlerReminderTests`.
- [ ] 5.2 Route both `DeliveryFailed` handlers through `ReportReminderPostFailure`. Write the Warning `cannot tell whether a reminder started turn {Turn}` for an unknown turn. Write a Warning for a fire key that does not parse. Verify with `ReminderTurnDeliveryFailureTests`.

## 6. Review round 1: routed-skill turns (S2, owner decision)

Tests exist and fail until the product code changes. Do not edit the tests.

- [ ] 6.1 Add `bool RoutedSkill` to `TurnRecorded` (default `false`), `bool routed_skill = 8;` to `TurnRecordedProto`, and the mapper. The live routed-skill path writes `true`. Verify with `SerializationRoundTripTests` (`TurnRecorded_routed_skill_mark_round_trips_as_a_plain_proto_bool`, `TurnRecorded_from_before_the_routed_mark_reads_as_not_routed`).
- [ ] 6.2 Change `DeliveryRetryHandler.RecordTurn` to `RecordTurn(TurnNumber, ReminderId?, bool routedSkill)` (no default value) and add `IsRoutedSkill(TurnNumber)`. Verify with `DeliveryRetryHandlerReminderTests`.
- [ ] 6.3 Add one helper, `RecordEligibleTurn(TurnRecorded)`, and call it from the live text turn, the live routed-skill turn, and `Recover<TurnRecorded>`. It passes `evt.RoutedSkill`.
- [ ] 6.4 In `HandleDeliveryFailedWhenReady`, skip the retry branch for a routed-skill turn: write a Warning, call `ReportReminderPostFailure`, and mark the turn not eligible. Make no model call. Verify with `RoutedSkillReminderDeliveryTests` and `A_recovered_ordinary_reminder_turn_still_retries_a_retryable_failure_on_the_main_model`.

## 7. Review round 1: snapshot (S3)

- [ ] 7.1 Move `MaybeSnapshot()` after `RecordEligibleTurn` in both live paths. Verify with `ReminderIntervalSnapshotRecoveryTests`.
- [ ] 7.2 Add `SessionSnapshot.EligibleDeliveryReminderId` and `optional string eligible_delivery_reminder_id = 12;` in `netclaw_messages.proto`, with the mapper. `BuildSnapshot` writes it only for an eligible, recorded, unreported reminder turn. `SnapshotOffer` calls `RecordTurn` when the reminder field has a value or the routed mark is true. Add `SessionSnapshot.EligibleDeliveryTurnRoutedSkill` and `bool eligible_delivery_turn_routed_skill = 13;`; `BuildSnapshot` writes the mark of the eligible turn and `SnapshotOffer` restores it, also for a routed turn that a person started. Verify with `SerializationRoundTripTests` and the passivation tests in `ReminderTurnDeliveryFailureTests` and `RoutedSkillReminderDeliveryTests`.

## 8. Review round 1: mutation target

- [ ] 8.1 Add a narrow Stryker target for `DeliveryRetryHandler.ReminderOf` and `TryMarkReported`. `DeliveryRetryHandlerReminderTests` is already linked into `Netclaw.Actors.MutationTests`. Add the script, the `mutation-gates` entry, the expected mutant count, and the `TOOLING.md` row.

## 9. Review round 1: docs

- [ ] 9.1 Update `netclaw-operations` `references/scheduling.md`. Say that the alert reaches only the configured notification webhooks, and that the daemon log has the Error line when no webhook exists. Bump `metadata.version` above the highest open-PR value (PR #2528 uses 2.111.0).
- [x] 9.2 Add `Directory.Packages.props`, `global.json`, `src/Netclaw.Actors/Channels/**`, and `src/Netclaw.Actors/Protocol/**` to the path filter of `.github/workflows/mattermost_integration.yml`.
- [x] 9.3 Fix the spec nits: blank line before each new requirement heading, the `Channel`-only evidence sentence in `netclaw-scheduling`, and the name of the job ledger in `background-job-execution`.
