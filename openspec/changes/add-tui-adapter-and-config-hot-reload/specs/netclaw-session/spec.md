## ADDED Requirements

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
