## MODIFIED Requirements

### Requirement: Mid-turn approval pause

The system SHALL pause individual tool execution tasks when approval is required
without blocking other tool calls in the same batch. The pause SHALL use a
`TaskCompletionSource` that completes when the session actor receives an approval
response. The pause SHALL wait indefinitely for user response — the system
SHALL NOT auto-deny on a timer. Operators take as long as they need to
evaluate a prompt; a clock-driven auto-deny silently transitions the
workflow to a denied state and manufactures race conditions (late clicks
landing in already-terminated workflows) for zero security benefit.

The set of pending tool interactions SHALL be persisted in the session
snapshot so the pause survives idle passivation, turn failure, and actor
restart. On recovery the session SHALL restore the pending interactions and,
when the approval response arrives, SHALL re-drive the paused tool batch from
the last assistant message rather than dropping the response. An approval
response whose call is not pending and cannot be reconstructed from session
history SHALL fail loud with a user-visible "approval prompt expired" message;
it SHALL NOT be silently discarded.

#### Scenario: Approval-pending tool blocks while others complete

- **GIVEN** a batch of 3 tool calls: `web_search`, `shell_execute`, `file_read`
- **AND** `shell_execute` requires approval
- **WHEN** the batch executes
- **THEN** `web_search` and `file_read` execute in parallel immediately
- **AND** `shell_execute` blocks waiting for approval
- **AND** the session actor remains responsive to messages

#### Scenario: Approval pause waits indefinitely for user response

- **GIVEN** an approval prompt has been emitted
- **AND** the user has not yet clicked any button
- **WHEN** an arbitrarily long time passes (minutes, hours, until daemon restart)
- **THEN** the workflow remains paused on the TaskCompletionSource
- **AND** no clock-driven transition to `TimedOut` occurs
- **AND** when the user eventually clicks, the workflow resumes from that state

#### Scenario: Approved tool executes and returns result

- **GIVEN** a tool is blocked waiting for approval
- **WHEN** the user approves (once or always)
- **THEN** the tool executes and returns its result
- **AND** the approval is cached (session-only or persistent depending on choice)

#### Scenario: Denied tool returns denial message

- **GIVEN** a tool is blocked waiting for approval
- **WHEN** the user denies
- **THEN** the tool returns "Command denied by user" as the tool result
- **AND** no command is executed

#### Scenario: Pending approval persisted to the session snapshot

- **GIVEN** a tool call has emitted an approval prompt and the turn is paused
- **WHEN** the session writes a snapshot
- **THEN** the snapshot SHALL include the pending tool interaction, keyed by call id
- **AND** the persisted interaction SHALL carry the requester identity, audience,
  and trust context needed to re-drive the call faithfully

#### Scenario: Pending approval survives idle passivation and cold recovery

- **GIVEN** a session with a pending approval prompt is idle-passivated and stopped
- **WHEN** the session is cold-respawned and recovers from its snapshot
- **THEN** the recovered session SHALL restore the pending tool interaction
- **AND** an approval response arriving afterward SHALL re-drive the tool batch
  and continue the turn
- **AND** the same requester-only `CanApprove` check and grant-persistence rules
  apply as on the live path

#### Scenario: Approval response for an expired call fails loud

- **GIVEN** a session has no pending interaction for the responded call id
- **AND** the call cannot be reconstructed from session history
- **WHEN** an approval response arrives for that call id
- **THEN** the session SHALL emit a user-visible message that the approval
  prompt has expired and the request should be re-issued
- **AND** the session SHALL NOT silently drop the response
