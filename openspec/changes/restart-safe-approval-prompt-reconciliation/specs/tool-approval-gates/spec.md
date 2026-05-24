## MODIFIED Requirements

### Requirement: Approval pause survives session recovery and reconciles visible prompts

The system SHALL pause individual tool execution tasks when approval is required
without blocking other tool calls in the same batch. The pause SHALL use a
`TaskCompletionSource` that completes when the session actor receives an approval
response. The pause SHALL wait indefinitely for user response.

Tool-batch start, per-tool results, approval requests, approval resolutions,
and abandonment closures SHALL be journaled so the pause survives idle
passivation, turn failure, and actor restart without relying on snapshots to
carry unjournaled in-flight state. On recovery the session SHALL restore pending
interactions from the journal and, when an approval response arrives, SHALL
re-drive only unresolved tool calls that are eligible to run.

For channels that support updating an already-posted approval prompt, the system
SHALL also preserve enough transport-side prompt metadata to reconcile the
visible prompt after passivation or restart. When the session resolves,
abandons, or expires a recovered approval interaction, the channel SHALL update
or disable the original prompt so it no longer appears as a live unresolved
approval.

An approval response whose call is not pending and cannot be reconstructed from
session history SHALL fail loud with a user-visible "approval prompt expired"
message; it SHALL NOT be silently discarded. If durable prompt metadata exists
for that call, the channel SHALL reconcile the original prompt into an expired
or otherwise non-interactive terminal state.

#### Scenario: Approval click after cold recovery re-drives tool and resolves original prompt

- **GIVEN** a tool call emitted an approval prompt in Slack, Mattermost, or Discord
- **AND** the session and channel binding were passivated or restarted before the user clicked
- **WHEN** the user clicks the original approval prompt after recovery
- **THEN** the session SHALL honor the approval using the restored pending interaction
- **AND** the channel SHALL reconcile the original prompt into a resolved non-interactive state

#### Scenario: Expired approval click after recovery fails loud and reconciles original prompt

- **GIVEN** a previously posted approval prompt whose call is no longer pending or reconstructable
- **WHEN** the user clicks the original prompt after passivation or restart
- **THEN** the session SHALL emit a user-visible "approval prompt expired" notice
- **AND** the channel SHALL reconcile the original prompt into an expired or disabled state when the platform supports updates

#### Scenario: Abandoned recovered prompt does not remain visually live

- **GIVEN** a session recovered with a parked approval prompt
- **AND** the user sends a new message that causes the parked tool batch to be abandoned
- **WHEN** the session records the abandonment
- **THEN** the original approval prompt SHALL be reconciled into a terminal non-interactive state when the platform supports updates
- **AND** a later click on the stale prompt SHALL not execute the tool
