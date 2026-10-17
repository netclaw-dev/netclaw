## Why

Since #2511, a `current_session` reminder run ends when the session admits the fire. The history row says `success`. If the reply post later fails, nobody sees the failure. A Mattermost or Discord post failure is a `TransportFailure`. The session does not retry it. The only trace is a Warning log line and a counter.

An operator who reads `netclaw reminder history` sees `ok` for a reminder that nobody received. On this path no person reads the thread. The rule "no silent fallbacks" applies. Issue #2535 found the gap.

## What Changes

- When the reply post of a turn that a reminder fire started fails for good, the session writes one Error log and raises one `reminder.delivery.failed` alert.
- The session finds the reminder through the durable `TurnRecorded.SourceReminderId`.
- The session does not write a history row. The reminder manager owns the history.
- `LlmSessionActor` gets a required `IOperationalNotificationSink` dependency.
- The history row of a `current_session` reminder records admission. A later post failure does not change it.
- The `netclaw-scheduling` scenario "A Mattermost post failure does not fail the reminder" names the log and the alert. The change `d2-reminders-end-at-admission` carries that text.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: a failed post on a reminder turn is visible to the operator.
- `reminder-execution-history`: the row of a `current_session` reminder records admission.

## Impact

- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` (`HandleDeliveryFailedWhenReady`) and the DI wiring of the session.
- `src/Netclaw.Configuration/OperationalAlert.cs`: a new `AlertType` value at the end of the enum.
- `netclaw-operations` system skill, `references/scheduling.md`: where a failed reminder post shows.
- No journal, snapshot, or config schema change.
- No authorization context changes. `docs/architecture/tool-authorization.md` needs no update.
