## Why

PRD-008 requires reliable reminder retries and useful failure signals. A busy session now accepts a CurrentSession reminder but cannot preserve its turn identity.

The reminder then waits one hour for an observation that cannot occur. A late gateway registration also creates a false permanent failure.

## What Changes

- Add a typed transient deferral response for CurrentSession reminder admission.
- Make each supported session binding defer a reminder before queue admission when its session has an active turn.
- Make the reminder execution actor report transient admission deferrals to the reminder manager.
- Make the manager use the Akka.Reminders negative acknowledgement path for durable backoff.
- Do not write failed history, increment `ConsecutiveFailures`, or emit failure alerts while Akka schedules another attempt.
- Count retry-budget exhaustion as one real reminder failure.
- Treat late registration for a supported origin gateway as a deferral.
- Keep permanent validation errors and accepted-turn delivery failures on the existing failure path.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: Define transient CurrentSession admission deferral and its retry and failure-count behavior.

## Impact

- **Source PRDs:** PRD-008.
- **In scope:** CurrentSession delivery through Slack, Discord, Mattermost, SignalR, and TUI session bindings.
- **Out of scope:** Channel delivery, no-delivery reminders, reminder health-count decay, and a new Akka.Reminders deferral API.
- **APIs:** Add one transient session response. No wire or persistence format changes apply.
- **Dependencies:** Continue to use `Aaron.Akka.Reminders` 0.7.0 and its existing `NackAsync` retry contract.
- **Security:** Preserve the current trusted reminder source, audience, boundary, and approval policy.
- **Operations:** Busy sessions cause bounded scheduler backoff. Scheduled deferrals do not appear as Netclaw execution failures.
