## Why

A channel binding can stop while its session still owns work. The session then loses its channel output path. Session idle policy must own the binding lifetime.

This change supports PRD-001 FR-001, FR-002, and FR-003, and PRD-009.

## What Changes

- Set the existing session idle timeout default to one hour.
- Keep idle eligibility in the session actor. Active work blocks idle passivation. Subscriber count does not.
- Keep journaled approval passivation and recovery unchanged.
- Emit `SessionDeactivated` only after the session commits to stop.
- Keep Slack, Discord, and Mattermost bindings alive until session deactivation. Each binding drains its pipeline and then stops.
- Remove the conversation parent's independent idle timeout. Stop it after its last binding child terminates.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-state-machine`: idle passivation uses the one-hour timeout and active-work guard. Subscriber count does not block passivation.
- `session-resume`: live subscribers do not veto passivation. Journaled approvals still passivate and recover through the current response route.
- `channel-binding-parity`: bindings stop after committed session deactivation. Conversation parents stop after their last binding child terminates.
- `background-job-execution`: active shell jobs block idle passivation. Reaped job records do not block it.

## Impact

The change affects session passivation and channel actor lifetime. It preserves current ingress routing, authorization, and approval recovery.

### Security impact

The change does not move ingress checks or authorization decisions.

### Operational impact

An active binding remains attached until its session commits to stop. An idle session can passivate with a journaled approval. A later approval response can rehydrate it through the current route.
