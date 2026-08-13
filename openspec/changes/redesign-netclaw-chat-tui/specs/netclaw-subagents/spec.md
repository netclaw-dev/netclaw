## MODIFIED Requirements

### Requirement: Subagent observability events

The system SHALL emit structured `SubAgentOutput` events to session subscribers
when a subagent starts, reports activity, and completes. These events SHALL be
filtered under the `OutputFilter.ToolCalls` category.

Each event SHALL include a stable `RunId` and the parent tool `CallId`. Activity
events SHALL include a safe phase label and MAY include a safe summary, tool
count, or elapsed duration. Terminal events SHALL include outcome and duration.

Sub-agent activity SHALL flow through the tool activity stream and session
output relay. It SHALL remain ephemeral and SHALL NOT enter model context or
persisted conversation history.

#### Scenario: Subagent start event emitted

- **GIVEN** a tool spawns a subagent within a session's tool execution pipeline
- **WHEN** the subagent begins execution
- **THEN** a `SubAgentOutput` event with `Phase = Started` is emitted
- **AND** the event includes `RunId`, parent `CallId`, agent name, and tool count
- **AND** the event is delivered to subscribers with `ToolCalls` in their filter

#### Scenario: Subagent activity event emitted

- **GIVEN** a subagent remains active
- **WHEN** its tool stream emits a safe progress update
- **THEN** a `SubAgentOutput` activity event uses the same `RunId` and parent
  `CallId`
- **AND** the update does not enter model context or persisted history

#### Scenario: Subagent completion event emitted

- **GIVEN** a subagent completes with success, failure, or cancellation
- **WHEN** the result is received by the calling tool
- **THEN** a `SubAgentOutput` event with `Phase = Completed` is emitted
- **AND** the event uses the same `RunId` and parent `CallId`
- **AND** the event includes outcome and duration

#### Scenario: Parallel same-name subagents remain distinct

- **GIVEN** two active subagents have the same definition name
- **WHEN** their activity and terminal events interleave
- **THEN** each event remains correlated by its distinct `RunId`
- **AND** neither terminal event settles the sibling run

#### Scenario: Headless CLI renders subagent events

- **GIVEN** the headless CLI subscribes with `OutputFilter.Full`
- **WHEN** a subagent starts, reports activity, and completes
- **THEN** the CLI renders machine-distinct start, activity, and completion
  records with the run identity

#### Scenario: Slack adapter suppresses subagent events

- **GIVEN** the Slack adapter excludes `ToolCalls` from its subscription
- **WHEN** a subagent starts, reports activity, and completes
- **THEN** no subagent-specific messages are posted to Slack
