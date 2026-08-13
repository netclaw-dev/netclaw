## ADDED Requirements

### Requirement: Tool activity output has stable call correlation

The session output contract SHALL carry nonterminal tool activity with the
parent session ID, turn identity, and `CallId`. A terminal tool result SHALL use
the same `CallId`. Parallel calls SHALL never share mutable presentation state.

Tool activity SHALL remain ephemeral. The actor SHALL NOT add it to model
context or persist it as conversation history.

#### Scenario: Parallel tool activity remains correlated

- **GIVEN** one model step starts calls A and B
- **WHEN** both calls emit interleaved activity
- **THEN** each activity output carries its original `CallId`
- **AND** each terminal result carries that same `CallId`

#### Scenario: Ephemeral activity does not enter model context

- **WHEN** a tool emits ten nonterminal activity updates and one terminal result
- **THEN** only the terminal result enters the model-facing tool message
- **AND** no nonterminal update enters persisted conversation history

### Requirement: Session output transport preserves all supported fields

Every field on a supported `SessionOutput` SHALL survive the in-process to
SignalR DTO boundary unless an explicit security rule removes it. The mapper
SHALL preserve usage detail, error identity, file metadata, compaction detail,
turn outcome, title, processing state, tool correlation, and sub-agent
correlation.

A mapper SHALL fail a contract test when a new output field lacks a deliberate
wire disposition.

#### Scenario: Compaction output crosses SignalR

- **WHEN** the session emits compaction output with cleared-result and summary
  counts
- **THEN** the SignalR client receives those same values

#### Scenario: Error output crosses SignalR

- **WHEN** the session emits an error with category, correlation ID, and detail
- **THEN** the SignalR client receives the same security-safe fields

#### Scenario: New field lacks a wire disposition

- **WHEN** a developer adds a field to a supported output record
- **THEN** the output parity contract test requires an explicit mapped or
  security-omitted disposition

### Requirement: Output filters apply to new activity events

Tool and sub-agent activity outputs SHALL use `OutputFilter.ToolCalls`.
Thought activity SHALL use `OutputFilter.Thinking`. Lifecycle and approval
events SHALL retain their existing mandatory delivery rules.

#### Scenario: Slack excludes tool activity

- **GIVEN** a Slack subscriber excludes `ToolCalls`
- **WHEN** a tool emits nonterminal activity
- **THEN** the Slack subscriber receives no activity output

#### Scenario: TUI receives full activity

- **GIVEN** a TUI subscriber requests the full output filter
- **WHEN** thought, tool, and sub-agent activity occurs
- **THEN** the TUI receives each permitted activity category
