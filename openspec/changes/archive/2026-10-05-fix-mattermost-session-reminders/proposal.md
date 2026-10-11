## Why

Netclaw rejects `current_session` reminders from Mattermost before persistence, although its gateway already supports delivery.
Issue [#1284](https://github.com/netclaw-dev/netclaw/issues/1284) exposes this gap in PRD-008 SCHED-001 and SCHED-004.

## What Changes

- Allow Mattermost in the existing current-session creation gate.
- Prove creation, persisted target identity, thread delivery, and delivery failure behavior.
- Correct the older gateway list in the schedule spec.
- Update the operational skill with the supported current-session routes.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-scheduling`: Include the existing Discord and Mattermost gateways in the current-session contract.

## Impact

The code change affects `SetReminderTool`. The driver and persistence format need no change.
The local proof uses the existing Docker fixture and real Mattermost post API.
The MVP scope includes existing session reminders. New schedule types and driver features remain outside this change.

## Security and operational impact

The reminder manager still validates the stored audience before persistence.
Tool calls still use the existing authorization path when the reminder executes.
The fix changes no grant scope or consent surface.
The tests use an isolated server and temporary state.
