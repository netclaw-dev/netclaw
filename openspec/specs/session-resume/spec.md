# session-resume Specification

## Purpose

Define session browsing, selection, and resumption behavior across TUI and CLI
entry points. Covers the daemon-side join path, client API surface, and TUI
session browser.
## Requirements

### Requirement: Session listing via REST API

The system SHALL expose session catalog data through the existing
`GET /api/sessions` REST endpoint. The `DaemonClient` SHALL query this endpoint
to retrieve recent sessions for display in the TUI or CLI.

#### Scenario: List recent sessions

- **WHEN** the client calls `ListSessionsAsync()`
- **THEN** a GET request is made to `/api/sessions`
- **AND** the response contains session entries with persistence ID, channel,
  title, turn count, last activity timestamp, and log path

#### Scenario: Daemon unreachable

- **WHEN** the client calls `ListSessionsAsync()` and the daemon is not running
- **THEN** the method throws or returns an empty list with a connection error
- **AND** no crash occurs in the TUI

### Requirement: Session resume via SignalR

The system SHALL allow a SignalR client to resume an existing session by passing
its session ID to `EnsureSession`. The daemon SHALL materialize a new
`SessionPipeline` against the provided session ID, triggering actor rehydration
from the journal if the session is passivated.

#### Scenario: Resume a passivated session

- **GIVEN** a session with ID `C07ABC/1234567890.123456` was previously active
  and has passivated
- **WHEN** a SignalR client calls `EnsureSession` with that session ID
- **THEN** the daemon materializes a `SessionPipeline` for that ID
- **AND** the session actor rehydrates from the Akka journal
- **AND** the client receives output from subsequent turns

#### Scenario: Resume a live session

- **GIVEN** a session is currently active with a Slack subscriber
- **WHEN** a SignalR client calls `EnsureSession` with that session ID
- **THEN** the daemon materializes a new `SessionPipeline` as an additional
  subscriber
- **AND** both the Slack and SignalR subscribers receive output independently

#### Scenario: Resume with invalid session ID

- **WHEN** a SignalR client calls `EnsureSession` with a session ID that does
  not exist in the catalog or journal
- **THEN** a new session is created with that ID
- **AND** the client can begin a fresh conversation

### Requirement: TUI session browser

The system SHALL provide a Terminal.Gui list view displaying recent sessions
from the catalog. The user SHALL be able to select a session to resume it in
the chat page.

#### Scenario: Open session browser

- **WHEN** operator runs `netclaw sessions`
- **THEN** the TUI displays a list of recent sessions
- **AND** each entry shows title (or "Untitled"), channel type, turn count, and
  relative last activity time

#### Scenario: Select session to resume

- **GIVEN** the session browser is displayed with entries
- **WHEN** the user selects a session and confirms
- **THEN** the TUI navigates to the chat page
- **AND** the chat page attaches to the selected session ID via `EnsureSession`

#### Scenario: No sessions available

- **GIVEN** the session catalog is empty
- **WHEN** the session browser loads
- **THEN** the TUI displays an empty state message
- **AND** offers to start a new chat session

### Requirement: CLI direct resume

The system SHALL support `netclaw chat --resume <session-id>` to skip the
session browser and open the chat page directly attached to the specified
session.

#### Scenario: Resume by ID

- **WHEN** operator runs `netclaw chat --resume C07ABC/1234567890.123456`
- **THEN** the chat page opens attached to the specified session
- **AND** the session actor rehydrates if passivated

#### Scenario: Resume with unknown ID

- **WHEN** operator runs `netclaw chat --resume nonexistent-id`
- **THEN** a new session is created with that ID
- **AND** the chat page opens with an empty conversation

### Requirement: Resumed session indicator

The system SHALL display a visual indicator when the chat page is attached to
a resumed session rather than a freshly created one.

#### Scenario: Show resumed session context

- **GIVEN** the user resumed a session with 5 prior turns and a title
- **WHEN** the chat page loads
- **THEN** a status message displays "Resumed: {title} (5 turns)"
- **AND** subsequent user input continues the conversation from the recovered
  state

### Requirement: Warm restart recovery for previously active sessions

The daemon SHALL persist the set of sessions that were active when a
config-triggered restart began and SHALL warm that set during startup after the
actor system is available. Warmed sessions SHALL recover through the normal
journal/snapshot path without requiring an immediate client-driven `EnsureSession`
call.

#### Scenario: Previously active session warms during startup recovery

- **GIVEN** a session was recorded as active in the restart manifest before shutdown
- **WHEN** the daemon starts again after the config-triggered restart
- **THEN** startup recovery re-creates that session through the session manager
- **AND** the session rehydrates from persisted state before normal traffic resumes

#### Scenario: Previously inactive session stays cold

- **GIVEN** a session was inactive when restart drain began
- **WHEN** the daemon starts again after the config-triggered restart
- **THEN** startup recovery does NOT proactively re-create that session
- **AND** the session remains lazily recoverable on its next normal resume or input

#### Scenario: Next turn receives restart continuity notice

- **GIVEN** a session was warmed from the restart manifest
- **WHEN** the next user turn begins after the daemon restart
- **THEN** the session injects a transient restart continuity notice into the turn context
- **AND** the notice explains that recovery resumed from the last durable checkpoint

### Requirement: Graceful stop of durable approval waits

During a graceful daemon stop, a session SHALL finish drain when every unfinished tool call waits on a durable approval. The session SHALL wait for the tool task to stop before it acknowledges drain. The journal SHALL retain approval requests and completed tool results for cold recovery.

Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: One durable approval stops promptly

- **GIVEN** a session has one unfinished tool call with a journaled approval request
- **WHEN** the daemon requests a graceful drain
- **THEN** the session stops the tool task and acknowledges drain without an approval response
- **AND** the original requester can approve the call after cold recovery
- **AND** the recovered turn keeps its original authority

#### Scenario: Completed sibling retains its result

- **GIVEN** one tool result has a journal record and another tool call waits on a journaled approval
- **WHEN** the daemon requests a graceful drain
- **THEN** the session acknowledges drain after the tool task stops
- **AND** recovery does not execute the completed sibling again

#### Scenario: Active sibling prevents the fast path

- **GIVEN** one tool call waits on a journaled approval and another tool call has no journaled result or approval
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path
- **AND** the current bounded drain path remains in effect

#### Scenario: Accepted buffered input prevents the fast path

- **GIVEN** a session has accepted user input in its actor buffer
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Non-durable or resolved approval prevents the fast path

- **GIVEN** an unfinished call has a non-durable approval or a resolved approval without a result
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Deferred approval response prevents the fast path

- **GIVEN** the session has a deferred approval response
- **WHEN** the daemon requests a graceful drain
- **THEN** the session does not acknowledge drain through the approval fast path

#### Scenario: Cancellation preserves the approval prompt

- **GIVEN** a durable approval prevents a tool call from execution
- **WHEN** graceful drain cancels the pipeline task
- **THEN** cancellation does not become a synthetic timeout or failed tool result
- **AND** the existing approval prompt remains unresolved
- **AND** its original button or text response resumes the recovered turn
- **AND** a duplicate response does not execute the tool again

### Requirement: Approval responses resume after idle passivation

When a session with a journaled approval passivates after its active work ends,
an approval response SHALL rehydrate the session and resume the original tool
batch. This recovery SHALL preserve the existing requester and restored-approval
checks. The session-state-machine capability defines whether idle passivation can
start.

#### Scenario: Approval response resumes the passivated turn

- **GIVEN** a session has a journaled approval and no active work when idle
  passivation starts
- **WHEN** the user responds to the approval prompt after passivation
- **THEN** the session rehydrates from the journal
- **AND** it re-drives the parked tool batch under the restored-approval
  requirements

#### Scenario: Recovery closes an approved call without a tool result

- **GIVEN** recovery finds a granted approval without a journaled tool result
- **WHEN** the session returns to `Ready`
- **THEN** the session persists a `ToolBatchAbandoned` event with a synthetic
  tool result
- **AND** clears the approval state
- **AND** does not execute the approved tool again

### Requirement: Accepted input survives a graceful stop

The session SHALL store each accepted input before acknowledgment. The record SHALL retain its identity, order, content, media, source identity, and original authority.

Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Input acknowledgment follows its journal record

- **GIVEN** a session receives user input
- **WHEN** the journal stores its admission record
- **THEN** the session acknowledges the input
- **AND** cold recovery restores the pending input and its authority

#### Scenario: Journal failure rejects input

- **GIVEN** the journal cannot store an admission record
- **WHEN** the session receives input
- **THEN** the session rejects that input
- **AND** it starts no model call for that input

#### Scenario: A lost acknowledgment does not duplicate input

- **GIVEN** the journal stores input with a stable source ID
- **WHEN** the source retries that input
- **THEN** the session acknowledges the stored input
- **AND** it does not add a second pending record

### Requirement: Graceful drain creates only a safe restart reminder

The session SHALL create a restart reminder only after an eligible model task stops. It SHALL use the existing reminder definition and `current_session` delivery contract.

#### Scenario: An interrupted model call creates a reminder

- **GIVEN** a model call has pending admitted input
- **AND** no tool batch or partial reply exists
- **WHEN** graceful drain cancels the call and confirms its task stopped
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder expires ten minutes after interruption

#### Scenario: A completed turn stays quiet

- **GIVEN** a model call completes during drain
- **AND** no admitted input remains pending
- **WHEN** the daemon starts again
- **THEN** it registers no restart reminder for that session

#### Scenario: A possible effect blocks the reminder

- **GIVEN** a tool batch started or partial text reached a subscriber
- **WHEN** graceful drain stops the session
- **THEN** the manifest contains no restart reminder for that turn
- **AND** the daemon reports the blocked session

### Requirement: A fresh restart reminder resumes stored work

The reminder manager SHALL deliver a fresh restart reminder through its existing `current_session` path. The session SHALL restore pending input under its recorded authority.

#### Scenario: A fresh reminder resumes the pending input

- **GIVEN** the restart manifest contains a reminder that has not expired
- **WHEN** the daemon starts
- **THEN** startup registers the reminder through `SaveReminderCommand`
- **AND** the session resumes the stored input without a user prompt

#### Scenario: The original authority remains in force

- **GIVEN** a restart reminder wakes a session with pending input
- **WHEN** the session starts the model call
- **THEN** it restores the recorded requester, audience, and trust boundary
- **AND** reminder automation authority does not replace that context

#### Scenario: An expired reminder stays quiet

- **GIVEN** the reminder expiration is in the past
- **WHEN** startup reads the restart manifest
- **THEN** it does not register or deliver that reminder
- **AND** it logs one warning

#### Scenario: Stored authority has no channel type

- **GIVEN** an interrupted session has no stored channel type
- **WHEN** graceful drain classifies the session
- **THEN** the actor creates no restart reminder
- **AND** the actor does not contain a channel-specific route list

### Requirement: SignalR text success confirms durable admission

The daemon SHALL return success for a text SendMessage request only after its journal stores the input admission record.
The daemon SHALL preserve the input's source identity and original authority.
The success response SHALL NOT wait for model completion.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Journal admission precedes the hub response

- **GIVEN** an authenticated connection is attached to its target session
- **AND** the journal holds the text admission write
- **WHEN** the connection sends text
- **THEN** the hub response remains incomplete until the journal stores that record
- **AND** success does not require a model response

#### Scenario: Buffered text retains the same contract

- **GIVEN** the session already processes or compacts an earlier turn
- **WHEN** an attached connection sends later text
- **THEN** success confirms that the journal stores the later input and its authority
- **AND** success does not require the earlier turn to finish

#### Scenario: An admission fault fails explicitly

- **GIVEN** session initialization, input enqueue, or the journal write fails
- **WHEN** a connection sends text
- **THEN** the daemon returns a failure or timeout
- **AND** the daemon does not report successful admission

#### Scenario: Attachment or ingress denial preserves authority

- **GIVEN** the connection lacks target attachment or ingress is closed
- **WHEN** the connection sends text
- **THEN** the daemon rejects the request before admission
- **AND** it starts no model call for that text

### Requirement: Client disconnect preserves admitted text work

The daemon SHALL retain admitted text after its SignalR client disconnects.
The disconnect SHALL NOT cancel the session's model task or discard the pending input.

#### Scenario: Disconnect precedes the model request

- **GIVEN** the daemon confirms text admission
- **AND** the model request has not started
- **WHEN** the client disconnects
- **THEN** the daemon retains that input
- **AND** the session can complete its turn or store an explicit terminal failure

#### Scenario: Disconnect occurs during the model response

- **GIVEN** the daemon confirms text admission and starts a model request
- **WHEN** the client disconnects before the model response
- **THEN** the session continues that turn independently of its subscriber
- **AND** the durable result does not require the client's connection

### Requirement: Text admission support is explicit

The daemon SHALL expose explicit support for durable text admission through its compatibility contract.
A client that depends on that support SHALL reject an unsupported daemon before it promises confirmed admission.

#### Scenario: A compatible daemon permits reliable admission

- **GIVEN** the daemon explicitly supports durable text admission
- **WHEN** the client attaches before text dispatch
- **THEN** the client can use the admission response contract

#### Scenario: An old daemon cannot imply support

- **GIVEN** the daemon lacks explicit durable text admission support
- **WHEN** the new client attaches before text dispatch
- **THEN** the client reports the unsupported daemon
- **AND** it does not silently treat the old early response as durable admission
