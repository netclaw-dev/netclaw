## Why

Since #2511, a `current_session` reminder run ends when the session admits the fire. The history row says `success`. If the reply post later fails, nobody sees the failure. A Mattermost or Discord post failure is a `TransportFailure`. The session does not retry it. The only trace is a Warning log line and a counter.

An operator who reads `netclaw reminder history` sees `ok` for a reminder that nobody received. On this path no person reads the thread. The rule "no silent fallbacks" applies. Issue #2535 found the gap.

## What Changes

- When the reply post of a turn that a reminder fire started fails for good, the session writes one Error log and raises one `reminder.delivery.failed` alert.
- The session reports one reply at most once, from one method (`ReportReminderPostFailure`), in `Ready` and in `Processing`.
- The session finds the reminder through `TurnRecorded.SourceReminderId`. It rebuilds the link from a live turn, from journal replay (a routed-skill turn included), and from a new snapshot field, `SessionSnapshot.EligibleDeliveryReminderId`.
- The session saves a snapshot after it records the turn as eligible, not before.
- A report that names a turn the session does not know gets a Warning. The session no longer drops it in silence.
- The Discord and Mattermost bindings send `DeliveryFailed` with the number of the turn whose post failed. They sent the number of the previous turn, so the session ignored the report. Slack already sent the right number. All three bindings now send one report for each turn, when `TurnCompleted` arrives.
- The session does not write a history row. The reminder manager owns the history.
- `LlmSessionActor` gets a required `IOperationalNotificationSink` dependency.
- The history row of a `current_session` reminder records admission. A later post failure does not change it.
- The `netclaw-scheduling` scenario "A Mattermost post failure does not fail the reminder" names the log and the alert. The change `d2-reminders-end-at-admission` carries that text.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: a failed post on a reminder turn is visible to the operator.
- `channel-binding-parity`: a failed post reports the turn that it belongs to.
- `reminder-execution-history`: the row of a `current_session` reminder records admission.

## Impact

- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` (`HandleDeliveryFailedWhenReady`) and the DI wiring of the session.
- `src/Netclaw.Configuration/OperationalAlert.cs`: a new `AlertType` value at the end of the enum.
- `netclaw-operations` system skill, `references/scheduling.md`: where a failed reminder post shows.
- `src/Netclaw.Channels/ChannelOutputEngine.cs`, `MattermostSessionBindingActor`, and `DiscordSessionBindingActor`: report at turn completion with the turn number of the turn.
- `SessionSnapshot` and `SessionSnapshotProto` get one optional field (`eligible_delivery_reminder_id`, field 12). An older snapshot loads with no value. No journal event and no config schema change.
- `.github/workflows/mattermost_integration.yml`: the path filter adds `Directory.Packages.props`, `global.json`, `src/Netclaw.Actors/Channels/**`, and `src/Netclaw.Actors/Protocol/**`.
- A focused mutation target for `DeliveryRetryHandler.ReminderOf` and `TryMarkReported` (see `TOOLING.md`, "Focused Mutation Tests").
- No authorization context changes. `docs/architecture/tool-authorization.md` needs no update.
