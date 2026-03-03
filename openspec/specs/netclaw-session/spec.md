# netclaw-session Specification

## Purpose

Define session identity, turn lifecycle, persistence recovery, subscriber
model, context management, and compaction behavior.

Research: `docs/research/context-management-patterns.md`

## Requirements

### Requirement: Slack thread session identity

The system SHALL key each session by `{channelId}/{threadTs}`.

#### Scenario: Route repeated thread messages to same actor

- **GIVEN** a thread session key already exists
- **WHEN** a new message arrives in the same thread
- **THEN** the same session actor handles the turn

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit typed output events to
subscribers. Subscriber delivery SHALL use a direct subscription model with
`OutputFilter` bitmask so that subscribers control which output categories they
receive (Text, Thinking, ToolCalls, Usage). Lifecycle events (TurnCompleted,
ErrorOutput, SessionTitleOutput) SHALL always be delivered regardless of filter.
`SubAgentOutput` events (Started/Completed phases) SHALL be filtered under the
`ToolCalls` category.

Multiple subscribers from different channels (e.g., Slack and TUI) SHALL
coexist on the same session actor. Each subscriber receives its own filtered
copy of output independently. Adding or removing a subscriber SHALL NOT affect
other active subscribers.

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

### Requirement: Context window usage transparency

The system SHALL include context window metadata in `UsageOutput` events so
subscribers can display usage percentage without duplicating session config.

#### Scenario: UsageOutput includes context window metadata

- **WHEN** a turn completes with usage data
- **THEN** `UsageOutput` includes `ContextWindowTokens` (total capacity) and
  `UsagePercent` (input tokens / context window)

### Requirement: Decoupled immutable session state

The system SHALL maintain conversation state (history, turn count, title) in an
immutable `SessionState` record decoupled from the actor. State transitions
SHALL be pure functions (`Apply` methods) testable without an ActorSystem.

#### Scenario: State transitions are pure and testable

- **GIVEN** a `SessionState` instance
- **WHEN** an event is applied via `Apply()`
- **THEN** a new `SessionState` is returned with the event applied
- **AND** the original instance is not modified

### Requirement: Session recovery across restart

The system SHALL recover session state from journal and snapshots.

#### Scenario: Recover context after process restart

- **GIVEN** prior persisted turns exist
- **WHEN** the process restarts
- **THEN** the session recovers prior context before processing new input

#### Scenario: Recover state after actor kill

- **GIVEN** two completed turns are persisted
- **WHEN** the session actor is killed and a new message arrives for the same session
- **THEN** a new actor recovers from the journal with TurnCount == 2
- **AND** the next turn continues from the recovered state

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach informed
by cross-SDK research (OpenAI, LangChain, Semantic Kernel, Anthropic, Google
ADK). Before compaction runs, the system SHALL trigger a pre-compaction memory
flush that persists durable memories (key facts, decisions, action items) to
external storage so they survive context reset.

#### Scenario: Compaction threshold reached

- **GIVEN** `UsageDetails.InputTokenCount` exceeds `SessionConfig.CompactionTokenLimit`
- **WHEN** compaction runs
- **THEN** actor enters `Compacting` behavior state
- **AND** incoming messages are buffered during compaction

#### Scenario: Tiered compaction — tool result clearing first

- **GIVEN** compaction is triggered
- **WHEN** phase 1 runs
- **THEN** old tool results are replaced with placeholders
- **AND** the N most recent tool interactions are preserved in full
- **AND** if threshold is now satisfied, no summarization LLM call is made

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) did not bring context under threshold
- **WHEN** phase 3 runs
- **THEN** a structured summarization LLM call is made with domain-specific
  section headings (task overview, current state, decisions, pending actions)
- **AND** a `SessionCompacted` event is persisted
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Pre-compaction memory flush

- **GIVEN** session history exceeds configured compaction threshold
- **WHEN** compaction is about to run
- **THEN** the system SHALL execute a silent agentic turn that extracts durable
  memories from the conversation
- **AND** persists them to external storage before context is reset

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** compaction runs
- **THEN** tool call/result pairs are never orphaned
- **AND** older tool interactions are summarized as structured entries

### Requirement: Tool context in session state

The system SHALL load available tools into session state based on the active
policy grants at session initialization. Tool definitions SHALL be refreshed
from the tool registry each time a session actor starts or recovers.

#### Scenario: Session loads granted tools at initialization

- **GIVEN** the ACL grants `shell`, `web_search`, and `mcp:memorizer` to the
  current channel and sender
- **WHEN** a session actor initializes
- **THEN** session state includes tool definitions for only the granted tool
  categories

#### Scenario: Denied tools excluded from session

- **GIVEN** the ACL does not grant `github` for the current channel
- **WHEN** a session actor initializes
- **THEN** GitHub tool definitions are not loaded into session state

### Requirement: Config hot-reload integration

The session system SHALL respond to config change notifications dispatched by
the `ConfigWatcherService`. Active sessions SHALL re-evaluate their tool grants
when ACL changes, rebuild provider connections when provider config changes,
and reconnect MCP servers when MCP profiles change.

#### Scenario: ACL change refreshes tool grants for active session

- **GIVEN** a session actor is active with tools loaded from the previous ACL
- **WHEN** the config watcher publishes an ACL change event
- **THEN** the session actor re-evaluates tool grants against the new ACL
- **AND** adds or removes tools from the session's available tool set

#### Scenario: Provider change triggers IChatClient rebuild

- **GIVEN** a session actor is using an `IChatClient` from the current provider
  configuration
- **WHEN** the config watcher publishes a provider change event
- **THEN** the session actor obtains a new `IChatClient` from the provider
  factory
- **AND** subsequent turns use the new provider configuration

#### Scenario: MCP profile change triggers server reconnection

- **GIVEN** a session actor has MCP tools loaded from connected servers
- **WHEN** the config watcher publishes an MCP profile change event
- **THEN** the session actor refreshes its MCP tool definitions
- **AND** newly added servers' tools become available
- **AND** removed servers' tools are no longer available

#### Scenario: Schedule change does not affect active sessions

- **GIVEN** a session actor is processing turns
- **WHEN** the config watcher publishes a schedule change event
- **THEN** the session actor does NOT take any action
- **AND** the `ScheduleManagerActor` handles timer reconfiguration independently
