# netclaw-session Delta Spec

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

The session actor SHALL create an `IApprovalChannel` instance at session start
and pass it to the tool execution pipeline. During the Processing behavior
phase, the session actor SHALL handle `ToolInteractionResponse` messages by
completing the corresponding `TaskCompletionSource` in the approval channel.
The session actor SHALL also update the `CommandApprovalCache` based on the
approval decision (session-scoped for ApproveOnce, persistent via
`ToolApprovalStore` for ApproveAlways).

The persisted `TurnRecorded` event SHALL carry an optional
`SourceReminderId` field (protobuf tag 5). When a `SendUserMessage` arrives
with `MessageSource.ReminderId` set, the resulting `TurnRecorded` event
SHALL copy that value into `SourceReminderId` so that reminder-originated
turns are distinguishable in the journal and survive recovery.

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

#### Scenario: Approval response handled during Processing

- **GIVEN** the session is in Processing phase with a pending approval
- **WHEN** a `ToolInteractionResponse` message arrives
- **THEN** the session actor completes the corresponding TCS in the approval
  channel
- **AND** the blocked tool task unblocks and proceeds based on the decision

#### Scenario: ToolInteractionRequest delivered as lifecycle event

- **GIVEN** a tool requires approval
- **WHEN** the pipeline emits a `ToolInteractionRequest`
- **THEN** all subscribers receive it regardless of their `OutputFilter`

## ADDED Requirements

### Requirement: Reminder redelivery dedup

`SessionState` SHALL maintain an in-memory
`ImmutableHashSet<string> ProcessedReminderIds`, folded in the
`Apply(TurnRecorded)` handler from each recovered or live event's
`SourceReminderId` when non-null. `LlmSessionActor` SHALL pre-check
`cmd.Source?.ReminderId` against this set at the top of both the `Ready`-phase
`HandleIncomingUserMessage` method and the `Processing`-phase
`Command<SendUserMessage>` buffer handler. On a dedup hit, the session SHALL
reply `CommandAck` to the sender without modifying state, persisting events,
or dispatching an LLM call.

The dedup check SHALL happen *before* any audience enforcement, ACL
evaluation, or prompt construction, so that a redelivery from
Akka.Reminders is handled entirely in memory and cannot trigger spurious
side effects.

#### Scenario: Redelivered reminder hits dedup in Ready phase

- **GIVEN** the session is in `Ready` phase with
  `ProcessedReminderIds = { "check-pr:1712000000000" }` recovered from the
  journal
- **WHEN** a `SendUserMessage` arrives with
  `MessageSource.ReminderId = "check-pr:1712000000000"`
- **THEN** the session replies `CommandAck` to the sender
- **AND** no `TurnRecorded` event is persisted
- **AND** the LLM is not invoked
- **AND** a `reminder_mode_b_dedup_hit` log entry is emitted

#### Scenario: Redelivered reminder hits dedup in Processing phase

- **GIVEN** the session is in `Processing` phase (LLM call in flight) with
  a dedup set containing `"nightly-report:1712005000000"`
- **WHEN** a `SendUserMessage` redelivery arrives with the same reminder ID
- **THEN** the session replies `CommandAck` without buffering the message
- **AND** the in-flight turn is unaffected

#### Scenario: Dedup set rebuilt on recovery

- **GIVEN** a session journal contains three `TurnRecorded` events, two with
  non-null `SourceReminderId` and one regular user turn
- **WHEN** the session actor recovers
- **THEN** `ProcessedReminderIds` contains exactly the two reminder IDs
- **AND** subsequent redeliveries of those reminders are deduped

#### Scenario: Non-reminder user messages are not deduped

- **GIVEN** a session with a populated `ProcessedReminderIds` set
- **WHEN** a regular `SendUserMessage` arrives with
  `MessageSource.ReminderId = null`
- **THEN** the message is processed normally regardless of the dedup set
