# netclaw-session Specification

## Purpose

Define session identity, turn lifecycle, persistence recovery, and compaction
behavior.

## Requirements

### Requirement: Slack thread session identity

The system SHALL key each session by `{channelId}/{threadTs}`.

#### Scenario: Route repeated thread messages to same actor

- **GIVEN** a thread session key already exists
- **WHEN** a new message arrives in the same thread
- **THEN** the same session actor handles the turn

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit a turn broadcast via
pub/sub. Broadcast delivery SHALL use a publish-subscribe pattern so that
multiple adapters (Slack, future web UI, timer adapter) can independently
consume turn events without the session actor knowing which adapters are
subscribed.

#### Scenario: Persist and broadcast assistant reply

- **WHEN** the assistant produces a response
- **THEN** a turn event is persisted
- **AND** a broadcast is published to the session topic for all subscribers

#### Scenario: Multi-adapter broadcast delivery

- **GIVEN** multiple adapters are subscribed to session broadcasts
- **WHEN** a turn broadcast is published
- **THEN** each subscribed adapter receives the broadcast independently
- **AND** the session actor does not reference specific adapter types

### Requirement: Session recovery across restart

The system SHALL recover session state from journal and snapshots.

#### Scenario: Recover context after process restart

- **GIVEN** prior persisted turns exist
- **WHEN** the process restarts
- **THEN** the session recovers prior context before processing new input

### Requirement: Conversation compaction

The system SHALL compact long session history using summary reduction. Before
compaction runs, the system SHALL trigger a pre-compaction memory flush that
persists durable memories (key facts, decisions, action items) to local memory
files so they survive context reset.

#### Scenario: Compaction threshold reached

- **GIVEN** session history exceeds configured threshold
- **WHEN** compaction runs
- **THEN** a compaction event is persisted
- **AND** compacted state remains usable for future turns

#### Scenario: Pre-compaction memory flush

- **GIVEN** session history exceeds configured compaction threshold
- **WHEN** compaction is about to run
- **THEN** the system SHALL execute a silent agentic turn that extracts durable
  memories from the conversation
- **AND** persists them to local memory files before context is reset

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
