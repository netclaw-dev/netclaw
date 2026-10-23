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

The session SHALL create a restart reminder only after an eligible model task or tool batch stops. It SHALL use the existing reminder definition and `current_session` delivery contract. The reminder SHALL carry exactly one recorded authority: the pending inputs first, then the oldest unreported child run.

A tool batch consumes the turn input. When the drain stops an eligible turn after its tool batch, the session SHALL journal one continuation input for that turn before it acknowledges the drain (#2632). The continuation input SHALL have no text and SHALL carry the recorded authority of the turn. The reminder then comes from that pending input. A turn is eligible only when all of these are true:

- No text of the stopped model call reached a subscriber.
- No other admitted input stays pending.
- No overflow replay waits. The rollback took the tool calls and results of the turn out of the history.
- No background job of the session is live. Passivation stops the job, and a resume could start it again.
- Each tool call of the turn has a recorded result. The drain never cancels a running tool.
- The turn has a channel type.

#### Scenario: An interrupted model call creates a reminder

- **GIVEN** a model call has pending admitted input
- **AND** no tool batch or partial reply exists
- **WHEN** graceful drain cancels the call and confirms its task stopped
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder expires ten minutes after interruption

#### Scenario: A turn stopped after its tool batch creates a reminder

- **GIVEN** a person turn ran three tool batches, and each tool call has a recorded result
- **AND** the next model call streams no text
- **WHEN** graceful drain stops the model call
- **THEN** the journal holds one pending continuation input without text and with the recorded authority of the turn
- **AND** the restart manifest stores one reminder that carries the recorded audience, boundary, and channel of the turn

#### Scenario: A completed turn stays quiet

- **GIVEN** a model call completes during drain
- **AND** no admitted input remains pending
- **WHEN** the daemon starts again
- **THEN** it registers no restart reminder for that session

#### Scenario: A possible effect blocks the reminder

- **GIVEN** a tool still runs at the coordinator deadline, or partial text of the stopped model call reached a subscriber
- **WHEN** graceful drain stops the session
- **THEN** the manifest contains no restart reminder for that turn
- **AND** the daemon reports the blocked session

#### Scenario: A live background job blocks the continuation

- **GIVEN** a turn started a background job, and the job still runs
- **WHEN** graceful drain stops the turn after its tool batch
- **THEN** the session journals no continuation input and posts the restart notice
- **AND** the session submits the job no second time

#### Scenario: An overflow replay blocks the continuation

- **GIVEN** a context overflow rolled the turn back into a replay, and compaction runs
- **WHEN** graceful drain stops the turn at the end of the compaction
- **THEN** the session journals no continuation input and posts the restart notice

### Requirement: Graceful drain stops a tool loop

The session SHALL apply the 2-second model grace period to each live model call of a turn. This rule applies also after a tool batch closed the inputs of the turn. After a drain request, the session SHALL start no model call and no tool batch. It SHALL keep the reminder rule of the requirement above. When the drain stops a turn and no restart reminder resumes that turn, the session SHALL post one text notice in its channel before it stops. A reminder resumes the stopped turn when it replays a pending input of the turn. After a tool batch, that input is the continuation input. A reminder that only reports a child run, or only runs a waiting input, does not resume the stopped turn. The shutdown alert SHALL name each session whose drain timed out.

#### Scenario: The model call after a tool batch stops after the grace period

- **GIVEN** a tool batch completed
- **AND** the next model call of the turn streams
- **WHEN** graceful drain starts
- **THEN** the session cancels the model call after the grace period
- **AND** it acknowledges drain with a restart reminder that resumes the turn

#### Scenario: A tool batch boundary stops the turn

- **GIVEN** a tool batch runs
- **WHEN** graceful drain starts, and the batch then completes
- **THEN** the session starts no model call
- **AND** it acknowledges drain with a restart reminder that resumes the turn

#### Scenario: Tool calls inside the grace period do not run

- **GIVEN** graceful drain started during a model call
- **WHEN** the model returns tool calls before the grace period ends
- **THEN** the session starts no tool batch
- **AND** it stops the turn as a model stop for restart does

#### Scenario: A waiting input keeps its reminder

- **GIVEN** a tool loop runs
- **AND** an accepted input waits in the journal
- **WHEN** graceful drain stops the turn
- **THEN** the restart manifest stores one reminder for that session
- **AND** the reminder runs the waiting input, and the stopped turn gets the notice

#### Scenario: A stopped turn without a reminder posts a notice

- **GIVEN** graceful drain stops a turn after a tool batch
- **AND** the turn has no recorded channel type
- **WHEN** the session passivates
- **THEN** the session posts one text notice in its channel before it stops
- **AND** the notice tells the person to send a message to continue

#### Scenario: A running tool posts no session notice

- **GIVEN** a tool still runs at the coordinator deadline
- **WHEN** the daemon stops
- **THEN** the session does not acknowledge the drain and posts no notice
- **AND** only the shutdown alert names the session

#### Scenario: A resumable turn posts no notice

- **GIVEN** graceful drain stops a model call that has pending admitted input, or a turn that is eligible after its tool batch
- **AND** no partial reply exists
- **WHEN** the session passivates
- **THEN** the session posts no restart notice

#### Scenario: The shutdown alert names a timed-out session

- **GIVEN** a session does not acknowledge drain before the drain deadline
- **WHEN** the daemon emits the shutdown alert
- **THEN** the alert context `timedOutSessionIds` names that session
- **AND** the drain log line does not claim that the session recovers

### Requirement: A fresh restart reminder resumes stored work

The reminder manager SHALL deliver a fresh restart reminder through its existing `current_session` path. The session SHALL restore pending input under its recorded authority. The continuation input of a stopped turn is a pending input. The session SHALL resume it as one, and it SHALL add no user message to the model history for it. The turn SHALL continue with the next model call over the stored history, under the recorded authority of the turn. The continuation input SHALL NOT start at recovery, and it SHALL NOT join the turn of another requester. The restart reminder is only the signal: an automation turn resumes under its own recorded automation authority, and a person turn under the recorded person authority.

#### Scenario: A fresh reminder resumes the pending input

- **GIVEN** the restart manifest contains a reminder that has not expired
- **WHEN** the daemon starts
- **THEN** startup registers the reminder through `SaveReminderCommand`
- **AND** the session resumes the stored input without a user prompt

#### Scenario: A fresh reminder resumes a turn stopped after its tool batch

- **GIVEN** the drain stopped a turn after its tool batch and journaled its continuation input
- **WHEN** the restart reminder arrives after the restart
- **THEN** the session replies `CommandAck` and starts the next model call of the turn
- **AND** the model request pairs each tool call with its result, holds the turn input once, and holds no empty user message

#### Scenario: Another person's input runs before the resume

- **GIVEN** the drain journaled a continuation input for Alice's turn
- **WHEN** Bob writes after the restart and before the restart reminder arrives
- **THEN** Bob's input runs in its own turn
- **AND** the restart reminder then resumes Alice's turn under Alice's authority

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

### Requirement: A restart-resume message is never an ordinary input

The session SHALL treat the restart reminder as a signal to resume stored work. The session SHALL NOT admit it as an input, and its text SHALL NOT reach the model as a user message.

- In `Ready` phase with pending input and no parked approval, the session SHALL resume that input.
- The continuation input of a stopped turn is a pending input. The resumed turn consumes it, so a redelivery after its first tool batch resumes nothing.
- In `Processing` or `Compacting` phase, the session SHALL queue each pending input that no turn selected and that the queue does not hold yet. It SHALL log `restart_resume_queued` and reply `RestartResumeNotNeeded`, also when it queued nothing. No turn of this message runs: each queued input runs later under its own recorded source. The normal drain SHALL run those inputs after the running turn. They run in admission order among themselves, after the inputs that the queue holds already. The child consent pause and the parent approval waits still apply.
- In `Ready` phase with nothing to resume, or in `Ready` phase when a parked approval owns the next turn, the session SHALL reply `RestartResumeNotNeeded` (a `CommandAck`), make no change, and log `restart_resume_noop`. This includes a redelivery after the resumed turn ended.
- `RestartResumeNotNeeded` SHALL tell the caller that this message resumes nothing and that no turn will run for it. A caller on the Reminder-origin route (#2491) SHALL then settle its run as success at once and SHALL NOT wait for a terminal turn.
- A reply that resumes a turn in `Ready` phase SHALL be an ordinary `CommandAck`, and the caller SHALL wait for the resumed turn. A reply that only queues work SHALL be `RestartResumeNotNeeded`, and the caller SHALL settle at once.
- A refusal (`CommandNack`) SHALL remain only for two causes: in `Ready` phase, a first pending input whose stored authority does not parse, and a restart reminder that arrives in `Processing`, `Compacting`, or `Passivating` phase while a restart drain is pending.
- A damaged pending input is an input whose stored authority does not parse. In each other case, the session SHALL log each damaged pending input, keep it pending, and queue the valid inputs (#2576). In `Ready` phase, a damaged input after the first input ends the leading inputs that the session resumes. The session SHALL NOT throw for a damaged input.
- In `Ready` phase, the session SHALL check for a parked approval before it checks the stored authority. In `Processing` or `Compacting` phase, the session does not check for a parked approval. It checks for a pending restart drain, and then it checks the stored authority.

#### Scenario: A redelivered restart message during the resumed turn

- **GIVEN** a restart reminder resumed the pending input and the turn runs
- **WHEN** the same restart reminder arrives again
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it stores no input

#### Scenario: A restart message with nothing pending

- **GIVEN** the resumed turn ended and the daemon restarted
- **WHEN** a redelivery of the restart reminder arrives
- **THEN** the session replies `RestartResumeNotNeeded`, not `CommandNack`
- **AND** it stores no input and makes no model call
- **AND** the resume text never reaches the model

#### Scenario: A second delivery after the resumed tool batch

- **GIVEN** the restart reminder resumed a stopped turn, and the resumed turn ran a tool batch
- **AND** the session actor stopped and recovered from the journal
- **WHEN** the same restart reminder arrives again
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it makes no model call

#### Scenario: A restart message during another turn queues the pending input

- **GIVEN** recovery started the turn of an orphan child result notice
- **AND** Alice's input P stays pending from before the restart
- **WHEN** the restart reminder arrives while the notice turn runs
- **THEN** the session queues P and replies `RestartResumeNotNeeded`
- **AND** a caller on the Reminder-origin route settles its run as success at once
- **AND** P runs in its own turn after the notice turn ends

#### Scenario: A restart message during another turn with nothing to queue

- **GIVEN** a turn runs, and every pending input is selected by that turn or is in the queue already
- **WHEN** the restart reminder arrives
- **THEN** the session replies `RestartResumeNotNeeded`
- **AND** it queues no input a second time

#### Scenario: A restart message with a damaged pending input during another turn

- **GIVEN** a turn runs, and a pending input from before the restart has a stored authority that does not parse
- **WHEN** the restart reminder arrives
- **THEN** the session logs the damaged input and keeps it pending
- **AND** it queues the valid inputs and replies `RestartResumeNotNeeded`

#### Scenario: A damaged pending input after the first input in Ready phase

- **GIVEN** an idle session with pending inputs `[A, damaged C, B]`
- **WHEN** the restart reminder arrives
- **THEN** the session resumes A and replies `CommandAck`
- **AND** it logs C, keeps C pending, and queues B

#### Scenario: A damaged first pending input in Ready phase

- **GIVEN** an idle session whose first pending input has a stored authority that does not parse
- **WHEN** the restart reminder arrives
- **THEN** the session replies `CommandNack`
- **AND** it starts no turn

#### Scenario: A no-op acknowledgement on the Reminder-origin route ends the run

- **GIVEN** a Reminder-origin turn resumed after a restart and its turn ended
- **WHEN** the restart reminder arrives again and the session replies `RestartResumeNotNeeded`
- **THEN** the reminder run settles as success at once
- **AND** it does not wait for the 20-minute stall timeout

#### Scenario: An acknowledgement that resumes a turn does not end the run

- **GIVEN** a Reminder-origin restart message with a pending input
- **WHEN** the session resumes the work and replies `CommandAck`
- **THEN** the run stays open until the resumed turn ends
