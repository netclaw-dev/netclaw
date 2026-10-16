## Why

A Mode B reminder (`current_session`) waits for a delivery result after the session admits the fire. The wait needs an observer in four binding actors, a one-hour backstop, and three copies of an in-memory dedup check. The dedup state leaks: a reservation stays set after the turn, and a drained fire can carry the wrong key. A failed post also retries the whole fire, which can post twice.

The session already stores each admitted input in its journal. The session can own what happens to the fire after admission.

## What Changes

- A Mode B run ends when the session acknowledges the fire (admission). The run no longer waits for a post.
- A session keeps at most one waiting fire per reminder. A newer fire of that reminder is acknowledged and dropped. A fire is not dropped while the earlier fire runs.
- One durable check at admission replaces `ProcessedReminderIds`, the in-flight reservation, and the three `IsReminderDuplicate` checks. The check reads the stored source key, so it also holds after snapshot recovery.
- A duplicate or dropped fire causes no side effect. It does not abandon an open tool batch.
- A restart-resume message is never an ordinary input. The session resumes pending input, or it acknowledges the message as a no-op and logs `restart_resume_noop`.
- Add `ReminderFireKey`, a reminder-owned value that parses the existing `"{id}:{ms}"` key. The journal shape does not change.
- Delete `DeliveryObserver`, `ReminderDeliveryResult`, `DeliveryObservedTimeout`, the delivery backstop, `ProcessedReminderIds`, and the observer code in `ChannelOutputEngine`, the Slack, Discord, and Mattermost binding actors, and `SignalRSessionActor`.
- **BREAKING**: a failed post for a Mode B reminder is no longer retried by the reminder system. `delivery_required` stays for `channel` delivery and has no effect for `current_session`. The session-level delivery retry stays.
- A dropped fire gets a log line (`reminder_fire_dropped`) only.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: a gateway `current_session` run succeeds at admission; delivery guarantees end at admission; the delivery observation timeout requirement is removed; the Mattermost scenarios no longer tie success to a post; a reminder has at most one waiting fire in a session.
- `netclaw-session`: reminder redelivery dedup becomes one durable check at admission.
- `session-resume`: a restart-resume message is never an ordinary input.
- `background-job-execution`: the job dedup requirement no longer mirrors `ProcessedReminderIds`.

## Impact

- `src/Netclaw.Actors`: `ReminderExecutionActor`, `ReminderProtocol`, `MessageSource`, `SessionInputFlow`, `InFlightTurnDedup`, `SessionState`, `LlmSessionActor`, new `ReminderFireKey`.
- `src/Netclaw.Channels`, `Netclaw.Channels.Slack`, `Netclaw.Channels.Discord`, `Netclaw.Channels.Mattermost`, `Netclaw.Daemon` (`SignalRSessionActor`): observer code removed.
- `netclaw-operations` system skill (`references/scheduling.md`): text for `delivery_required`.
- No journal, snapshot, or config schema change.
- A TUI reminder that fires with no client runs, and its output stays in the session transcript.
