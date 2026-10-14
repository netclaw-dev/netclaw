## ADDED Requirements

### Requirement: Session input progress has a durable boundary

The session SHALL keep an admitted ordinary input pending until accepted durable progress consumes it or an explicit closure event closes it. `ToolBatchStarted` SHALL consume the input into durable turn history. `TurnRecorded` SHALL complete the turn. `InputClosed` SHALL close an input explicitly.

The session SHALL validate state transitions before it appends their events. A rejected transition SHALL not add an invalid event to the journal. Child results SHALL use the child-result contract and SHALL NOT enter ordinary user-input admission.

#### Scenario: Tool batch start consumes its pending input

- **GIVEN** an ordinary input remains pending in the durable session state
- **WHEN** `ToolBatchStarted` records accepted turn progress
- **THEN** the input leaves the pending queue
- **AND** its content remains in durable turn history

#### Scenario: Completed turn records its terminal event

- **GIVEN** `ToolBatchStarted` consumed the input
- **WHEN** the turn completes
- **THEN** the session records `TurnRecorded`
- **AND** replay reports the turn as complete

#### Scenario: Explicit closure removes an unconsumed input

- **GIVEN** an input remains pending and cannot proceed
- **WHEN** the session records `InputClosed`
- **THEN** replay reports the input as explicitly closed
- **AND** the session does not silently discard it

#### Scenario: Invalid transition does not poison the journal

- **GIVEN** a proposed transition violates the current durable input order
- **WHEN** the session validates the transition
- **THEN** it rejects the transition before journal append
- **AND** a later recovery can replay the journal

#### Scenario: Child result bypasses ordinary input admission

- **GIVEN** a child result becomes ready for its parent
- **WHEN** the parent accepts that result
- **THEN** the session does not persist it as an ordinary user input
- **AND** the result follows the correlated tool-result path

## MODIFIED Requirements

### Requirement: Persisted turn lifecycle

For a child run with durable acceptance, the session actor SHALL persist the
child approval request and each accepted resolution in the parent journal.
The request SHALL retain the child's original authority and requester. The
actor SHALL persist an accepted resolution before it releases the child's
blocked tool call.

A child approval wait without durable child-run acceptance SHALL remain
live-only and SHALL create no parent approval journal event.

#### Scenario: Persist and emit assistant reply

- **WHEN** the assistant produces a response
- **THEN** a `TurnRecorded` event is persisted
- **AND** typed output events are emitted to subscribers based on their filter

#### Scenario: Reminder-originated turn carries SourceReminderId

- **GIVEN** the session receives a `SendUserMessage` whose
  `MessageSource.ReminderId` equals `"daily-digest:1712000000000"`
- **WHEN** the turn completes and `TurnRecorded` is persisted
- **THEN** the persisted event has
  `SourceReminderId = "daily-digest:1712000000000"`
- **AND** the event is replayable as a normal turn on recovery

#### Scenario: Non-reminder turn has null SourceReminderId

- **GIVEN** the session receives a regular user `SendUserMessage` with
  `MessageSource.ReminderId = null`
- **WHEN** the turn completes and `TurnRecorded` is persisted
- **THEN** the persisted event has `SourceReminderId = null`

#### Scenario: Multi-subscriber filtered delivery

- **GIVEN** multiple subscribers with different OutputFilter bitmasks
- **WHEN** a turn completes with text, thinking, and usage data
- **THEN** each subscriber receives only the output categories matching their filter
- **AND** all subscribers receive lifecycle events regardless of filter

#### Scenario: Cross-channel multi-subscriber

- **GIVEN** a session originally created by the Slack channel with an active
  Slack subscriber
- **WHEN** a TUI client joins the same session via `JoinSession`
- **THEN** both Slack and TUI subscribers receive output from subsequent turns
- **AND** either subscriber disconnecting does NOT affect the other
- **AND** the session continues processing input from any attached channel

#### Scenario: Parent approval response handled during Processing

- **GIVEN** the parent session is in Processing phase with a pending approval
- **WHEN** a valid `ToolInteractionResponse` message arrives
- **THEN** the workflow returns an accepted response outcome
- **AND** the actor journals the resolution before the workflow completes the live wait
- **AND** the blocked tool call proceeds from the accepted decision

#### Scenario: Subagent approval response stays live-only without durable acceptance

- **GIVEN** a subagent has a live approval wait through its parent session
- **AND** the child has no durable run acceptance
- **WHEN** a valid `ToolInteractionResponse` message arrives
- **THEN** the workflow completes that live wait
- **AND** the actor journals neither a parent approval request nor a resolution

#### Scenario: Durable child approval facts precede release

- **GIVEN** a child run has durable acceptance with its original requester and authority
- **AND** the parent journal contains the child's correlated approval request
- **WHEN** the original requester sends a valid approval response
- **THEN** the parent journal records the correlated approval resolution
- **AND** the request retains the child's original requester and authority
- **AND** the actor releases the blocked child tool call only after it persists the resolution

#### Scenario: ToolInteractionRequest delivered as lifecycle event

- **GIVEN** a tool requires approval
- **WHEN** the pipeline emits a `ToolInteractionRequest`
- **THEN** all subscribers receive it regardless of their `OutputFilter`

#### Scenario: ApproveOnce permits the exact blocked call

- **GIVEN** a parent session has a blocked tool call with an approval prompt
- **WHEN** the requester chooses `ApproveOnce`
- **THEN** the exact blocked call can retry once with that consent
- **AND** the session creates no session or persistent grant

#### Scenario: ApproveOnce does not approve a later call

- **GIVEN** the requester used `ApproveOnce` for a blocked tool call
- **WHEN** a later call needs approval for the same candidate
- **THEN** that later call requires a new approval
