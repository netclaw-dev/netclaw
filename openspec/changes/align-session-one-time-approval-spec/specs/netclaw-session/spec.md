## MODIFIED Requirements

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit typed output events to
subscribers. Subscriber delivery SHALL use a direct subscription model with
`OutputFilter` bitmask so that subscribers control which output categories they
receive (Text, Thinking, ToolCalls, Usage). Lifecycle events (TurnCompleted,
ErrorOutput, SessionTitleOutput, ToolInteractionRequest) SHALL always be
delivered regardless of filter. `SubAgentOutput` events (Started/Completed
phases) SHALL be filtered under the `ToolCalls` category.

Multiple subscribers from different channels (e.g., Slack and TUI) SHALL
coexist on the same session actor. Each subscriber receives its own filtered
copy of output independently. Adding or removing a subscriber SHALL NOT affect
other active subscribers.

The `SessionToolWorkflow` SHALL own the session-local `IApprovalChannel` and
pass it to the tool execution pipeline. During Processing, the session actor
SHALL route `ToolInteractionResponse` messages to the workflow. The workflow
SHALL validate each response and return a typed outcome. For an accepted
parent-session answer, the actor SHALL journal the resolution before the
workflow releases the blocked call. A subagent approval wait SHALL remain
live-only and SHALL not create a parent approval journal event. The session
actor SHALL record a grant answer through `IToolApprovalService` with the
selected grant scope.
An `ApproveOnce` answer SHALL let only the exact blocked call retry once. It
SHALL create no session or persistent grant.

The persisted `TurnRecorded` event SHALL carry an optional
`SourceReminderId` field (protobuf tag 5, additive). When a
`SendUserMessage` arrives with `MessageSource.ReminderId` set, the
resulting `TurnRecorded` event SHALL copy that value into
`SourceReminderId` so that reminder-originated turns are distinguishable
in the journal (for forensics) and survive recovery (so the in-memory
dedup set can be rebuilt via event replay).

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

#### Scenario: Subagent approval response stays live-only

- **GIVEN** a subagent has a live approval wait through its parent session
- **WHEN** a valid `ToolInteractionResponse` message arrives
- **THEN** the workflow completes that live wait
- **AND** the actor does not journal a parent approval resolution

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
