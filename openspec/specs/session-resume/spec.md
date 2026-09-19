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

The daemon SHALL persist the set of sessions that were active when a graceful
stop began. It SHALL warm that set in the background after startup.
Warmed sessions SHALL recover through the normal journal and snapshot path.
They SHALL NOT require an immediate client-driven `EnsureSession` call.

#### Scenario: Previously active session warms during startup recovery

- **GIVEN** a session was recorded as active in the restart manifest before shutdown
- **WHEN** the daemon starts again after the graceful stop
- **THEN** startup recovery re-creates that session through the session manager
- **AND** the session rehydrates from persisted state without delaying daemon readiness

#### Scenario: Previously inactive session stays cold

- **GIVEN** a session was inactive when restart drain began
- **WHEN** the daemon starts again after the graceful stop
- **THEN** startup recovery does NOT proactively re-create that session
- **AND** the session remains lazily recoverable on its next normal resume or input

#### Scenario: Next turn receives restart continuity notice

- **GIVEN** a session was warmed from the restart manifest
- **WHEN** the next user turn begins after the daemon restart
- **THEN** the session injects a transient restart continuity notice into the turn context
- **AND** the notice explains that recovery resumed from the last durable checkpoint

### Requirement: Outstanding tool approvals restored on session recovery

When a session recovers, it SHALL restore outstanding tool approvals from
journaled tool-batch and approval events. Snapshots SHALL remain a cache of
state already implied by earlier journal events; they SHALL NOT be the source of
truth for in-flight approval state.

#### Scenario: Pending approval restored from journal on recovery

- **GIVEN** a `ToolApprovalRequested` event was written while a tool-approval prompt was outstanding
- **WHEN** the session cold-recovers through the journal/snapshot path
- **THEN** recovery SHALL restore the pending tool interaction from the journal
- **AND** the session SHALL log the count of recovered pending interactions

#### Scenario: Restored pending approval superseded by resolution events

- **GIVEN** a journal carrying a pending tool interaction
- **WHEN** a `ToolApprovalResolved`, `ToolBatchAbandoned`, `TurnRecorded`, or `SessionCompacted` event replays afterward
- **THEN** recovery SHALL clear the superseded pending interaction
- **AND** the recovered session SHALL NOT treat the superseded approval as outstanding

#### Scenario: Approval click resumes a cold-resumed session

- **GIVEN** a tool approval prompt was outstanding when the session passivated
- **WHEN** the session cold-resumes and the user clicks the approval afterward
- **THEN** the session SHALL re-drive the parked tool batch and continue the turn
- **AND** the user SHALL NOT have to send a separate message to wake the agent

#### Scenario: Pre-change snapshot recovers with no pending approval projection

- **GIVEN** a snapshot written before approval recovery existed
- **WHEN** the session recovers from that snapshot
- **THEN** recovery SHALL succeed with an empty pending-interaction set
- **AND** SHALL NOT fail or error on the missing field

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

### Requirement: Recovered pending approvals restore turn context

When a session recovers pending tool approvals from the journal, it SHALL also restore the original turn context for each pending approval. The restored context SHALL include the requester, audience, boundary, channel type, approval capability, principal classification, provenance, and adopted-context safety state needed to resume the original request faithfully.

#### Scenario: Pending approval restores original context

- **GIVEN** a `ToolApprovalRequested` event was written with turn context while a prompt was outstanding
- **WHEN** the session cold-recovers through the journal path
- **THEN** the pending approval is restored with that turn context
- **AND** approval redrive uses the restored context rather than deriving a new context from the session id

#### Scenario: Legacy approval event uses compatibility restoration

- **GIVEN** a pre-change `ToolApprovalRequested` event has no turn-context record but still has legacy persisted trust fields
- **WHEN** the session recovers the event
- **THEN** the session MAY construct turn context from those legacy fields
- **AND** this compatibility path is isolated from the normal new-event path

#### Scenario: Incomplete legacy approval fails loud

- **GIVEN** a recovered pending approval lacks enough persisted context to restore the original requester and trust context
- **WHEN** an approval response arrives for that pending call
- **THEN** the session does not redrive the tool under a synthesized permissive context
- **AND** the user receives an explicit expired or unrecoverable approval notice

### Requirement: Restarted approvals resume as the original request

An approval response received after idle passivation, cold recovery, or daemon restart SHALL resume the same original session turn. The resumed tool batch and any continuation LLM/tool calls SHALL use the restored turn context until the resumed turn completes or is abandoned.

#### Scenario: Approval after cold recovery resumes original request

- **GIVEN** a session has recovered a pending approval from the journal
- **WHEN** the original requester approves the prompt
- **THEN** the session re-drives the parked tool batch
- **AND** the tool execution context uses the original turn audience, boundary, channel type, and approval capability

#### Scenario: Continuation after redrive keeps restored context

- **GIVEN** a recovered approval redrive has completed its parked tool call
- **WHEN** the follow-up LLM response asks for another tool call in the same turn
- **THEN** the continuation tool call uses the same restored turn context
- **AND** it does not fall back to a context derived from missing transport metadata

### Requirement: Idle passivation proceeds with pending approvals

A session SHALL NOT defer idle passivation because tool approval prompts are
outstanding. Pending approval state is journaled (`ToolApprovalRequested` /
`ToolApprovalResolved`) and the approval response path already rehydrates a
passivated session and resumes the original turn, so keeping the session in
memory while a human decides adds no correctness — only resident memory.
Active live subscribers (CLI/TUI connections) SHALL continue to defer
passivation, because subscriber connections are ephemeral and cannot survive
actor stop. The existing resolved-approval abandonment behavior (a parked tool
batch whose approval was granted but whose tool result never completed) SHALL
be preserved.

#### Scenario: Session passivates with an approval prompt outstanding

- **GIVEN** a session is idle past its idle timeout
- **AND** a tool approval prompt is outstanding
- **AND** no live subscribers are attached
- **WHEN** the receive timeout fires
- **THEN** the session passivates normally
- **AND** the pending approval remains recoverable from the journal

#### Scenario: Approval click after passivation resumes the turn

- **GIVEN** a session passivated with an approval prompt outstanding
- **WHEN** the user responds to the approval prompt
- **THEN** the session rehydrates from the journal
- **AND** re-drives the parked tool batch per the existing restored-approval
  requirements

#### Scenario: Live subscribers still defer passivation

- **GIVEN** a session is idle past its idle timeout
- **AND** a live CLI or TUI subscriber is attached
- **WHEN** the receive timeout fires
- **THEN** passivation is deferred while the subscriber remains attached

### Requirement: Accepted input survives a graceful stop

The session SHALL record each accepted input before it acknowledges that input. The record SHALL retain a stable input ID, source ID, order, text, media, authority, and delivery context. A failed journal write SHALL reject the input.

#### Scenario: Input ack follows its journal record

- **GIVEN** a user sends input to a ready session or a busy session
- **WHEN** the journal confirms the accepted input record
- **THEN** the session acknowledges the input
- **AND** cold recovery restores its text, media, order, and original authority

#### Scenario: Failed journal write rejects input

- **GIVEN** the journal cannot store an input record
- **WHEN** the user sends that input
- **THEN** the session rejects the input with a visible error
- **AND** the session does not start a model call for it

#### Scenario: Source retry does not duplicate accepted input

- **GIVEN** the journal stores an input with a stable source message ID
- **WHEN** the source retries that same message after it loses the ack
- **THEN** the session acknowledges the existing input
- **AND** the session does not add a second copy to the queue

### Requirement: Graceful stop records only safe resume candidates

The daemon SHALL record a resume candidate after any graceful stop only for confirmed interrupted model work or accepted queued input. The candidate SHALL have one absolute deadline ten minutes after interruption. A stopped model task SHALL be confirmed before the session acknowledges drain.

#### Scenario: Interrupted model call creates a candidate

- **GIVEN** an admitted turn has a live model call and no tool batch has started in that turn
- **WHEN** a graceful stop interrupts the call after a short completion grace
- **THEN** the session waits for the model task to stop
- **AND** the daemon records a candidate for that original turn

#### Scenario: Completed reply with queued input creates a candidate

- **GIVEN** a turn finishes during drain and accepted queued input remains
- **WHEN** the session acknowledges drain
- **THEN** the daemon records a candidate for the accepted queue

#### Scenario: Completed reply without queued input stays quiet

- **GIVEN** a turn finishes during drain and no accepted queued input remains
- **WHEN** the daemon starts again
- **THEN** the daemon starts no model call for that session

#### Scenario: Tool effect or partial reply blocks automatic resume

- **GIVEN** the interrupted turn has a tool batch or user-visible partial text
- **WHEN** the daemon stops
- **THEN** the daemon does not record an executable model resume candidate
- **AND** it reports the blocked work to the operator

### Requirement: Eligible restart resumes original work once

The daemon SHALL resume an eligible candidate only before its deadline and after its output route is ready. The session SHALL use the recorded authority and input IDs. It SHALL not create a new reminder turn or ask the user for stored context.

#### Scenario: Cold recovery resumes the original turn

- **GIVEN** a graceful stop confirmed model cancellation and stored the admitted input
- **WHEN** the daemon starts before the candidate deadline
- **THEN** the session resumes one model call for the original turn
- **AND** it uses the recorded requester and trust boundary

#### Scenario: Accepted queue follows the original turn

- **GIVEN** a canceled turn and multiple accepted queued messages
- **WHEN** the resumed original turn finishes
- **THEN** the session sends the queued messages in their original order
- **AND** it uses one follow-up model call for that queue

#### Scenario: Expired candidate stays quiet

- **GIVEN** the absolute candidate deadline has passed
- **WHEN** the daemon starts or tries to resume the session
- **THEN** it starts no model call from that candidate
- **AND** it reports and removes the expired candidate once

#### Scenario: Output route is absent

- **GIVEN** a candidate has no live route that can deliver the resumed output
- **WHEN** the daemon starts before its deadline
- **THEN** it does not run a model call yet
- **AND** it reports the blocked candidate to the operator

#### Scenario: Newer work supersedes a candidate

- **GIVEN** a newer user turn starts before the recovery request reaches the session
- **WHEN** the older recovery request arrives
- **THEN** the session rejects the stale request and starts no duplicate call

#### Scenario: Incompatible queued authority blocks automatic resume

- **GIVEN** accepted queued messages have incompatible trust boundaries
- **WHEN** the daemon tries to resume that queue
- **THEN** the session keeps the messages in the journal
- **AND** it reports the blocked queue instead of widening tool authority
