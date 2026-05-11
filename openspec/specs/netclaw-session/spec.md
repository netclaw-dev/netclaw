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

### Requirement: Persisted adopted-context metadata separates truthful provenance from third-party policy

When the session persists or reuses an adopted-context record, it SHALL preserve
the full adopted window truthfully and SHALL NOT collapse self-only adopted
history into "no adopted context."

For persisted session metadata:

- `HasAdoptedContext` SHALL mean the adopted window is non-empty.
- Adopted-speaker provenance SHALL include all sender ids present in that
  adopted window.
- `HasThirdPartyAdoptedContext` SHALL be tracked as a separate policy concept and
  SHALL be true only when any adopted sender id differs from the current
  authorized author of the executable message.

This metadata split SHALL coexist with the existing trust model that adopted
context is quoted, non-executable context and only the current authorized
message is executable.

#### Scenario: Persisted record keeps self-only adopted window truthful

- **GIVEN** an adopted-context record is written for an authorized turn
- **AND** every adopted sender id matches the current authorized sender
- **WHEN** the session persists the record
- **THEN** `HasAdoptedContext` is true
- **AND** adopted-speaker provenance includes that sender id
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Persisted record marks third-party policy separately

- **GIVEN** an adopted-context record is written for an authorized turn
- **AND** the adopted window includes a sender id different from the current
  authorized sender
- **WHEN** the session persists the record
- **THEN** adopted-speaker provenance includes all adopted sender ids
- **AND** `HasThirdPartyAdoptedContext` is true

#### Scenario: Adopted context remains non-executable after metadata split

- **GIVEN** a persisted record reports `HasAdoptedContext=true`
- **WHEN** the session later uses that record for audit, retry, or recovery
- **THEN** the adopted window remains quoted, non-executable context
- **AND** only the current authorized message remains executable
