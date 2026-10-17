## 1. Alert and sink

- [ ] 1.1 Add `AlertType.ReminderDeliveryFailed` at the end of the enum.
- [ ] 1.2 Give `LlmSessionActor` a required `IOperationalNotificationSink`; update every construction site; verify the solution builds.

## 2. Session behavior

- [ ] 2.1 In `HandleDeliveryFailedWhenReady`, read the reminder fire key of the named turn from the recorded turn data; verify with `ReminderTurnDeliveryFailureTests`.
- [ ] 2.2 On a final failure of a reminder turn, write the Error log and emit `reminder.delivery.failed`; write no history row; verify with `ReminderTurnDeliveryFailureTests`.
- [ ] 2.3 Keep the alert off for a turn with no reminder source and for a retryable failure that a retry recovers; verify with `ReminderTurnDeliveryFailureTests`.
- [ ] 2.4 Raise the alert when the retry budget ends on a reminder turn; verify with `ReminderTurnDeliveryFailureTests`.
- [ ] 2.5 Find the reminder after the session recovers from its journal; verify with `ReminderTurnDeliveryFailureTests`.

## 3. Docs and proof

- [ ] 3.1 Update `netclaw-operations` `references/scheduling.md` and bump `metadata.version`.
- [ ] 3.2 Run `MattermostReminderIntegrationTests` with `NETCLAW_RUN_MATTERMOST_INTEGRATION_TESTS=1`.
- [ ] 3.3 Run the Actors suite, slopwatch, and the header check.
