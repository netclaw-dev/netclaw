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
The session actor SHALL also record approvals through `IToolApprovalService`
based on the approval decision (session-scoped for ApproveOnce, persistent for
ApproveAlways).

#### Scenario: Persist and emit assistant reply

- **WHEN** the assistant produces a response
- **THEN** a `TurnRecorded` event is persisted
- **AND** typed output events are emitted to subscribers based on their filter

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
