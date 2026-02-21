# netclaw-config-hot-reload Specification

## Purpose

Define hot-reload behavior for operational configuration files. The system
monitors ACL rules, provider configuration, MCP server profiles, and schedule
definitions for changes and applies them to the runtime without process restart.

## Requirements

### Requirement: Operational config file monitoring

The system SHALL monitor operational configuration files for changes using
`FileSystemWatcher`. Monitored files SHALL include ACL rules, provider
configuration, MCP server profiles, and schedule definitions.

#### Scenario: ACL file change detected

- **GIVEN** the ACL rules file exists at the configured path
- **WHEN** the file is modified on disk
- **THEN** the `ConfigWatcherService` detects the change within 500ms

#### Scenario: Provider config change detected

- **GIVEN** the provider configuration file exists at the configured path
- **WHEN** the file is modified on disk
- **THEN** the `ConfigWatcherService` detects the change within 500ms

#### Scenario: Unwatched files are not monitored

- **GIVEN** personality files, project registry, or environment inventory files
  exist
- **WHEN** those files are modified on disk
- **THEN** the `ConfigWatcherService` does NOT detect or process the change

### Requirement: Change event debounce

The system SHALL debounce file change events with a configurable window
(default 500ms) to prevent rapid-fire reloads during file save operations.

#### Scenario: Rapid successive writes debounced

- **GIVEN** a watched config file is being saved
- **WHEN** the file system emits multiple change events within 500ms
- **THEN** the system processes only one reload after the debounce window

#### Scenario: Separate files reload independently

- **GIVEN** ACL rules and provider config are both watched
- **WHEN** both files change within the debounce window
- **THEN** each file's change is processed independently after its own debounce

### Requirement: Validate before apply

The system SHALL validate changed configuration before applying it to the
runtime. Invalid configuration SHALL be rejected with logged diagnostics.
The previous valid configuration SHALL remain in effect.

#### Scenario: Valid config change applied

- **GIVEN** a watched config file changes
- **WHEN** the new content passes validation
- **THEN** the change is applied to the runtime
- **AND** owning actors are notified

#### Scenario: Invalid config change rejected

- **GIVEN** a watched config file changes
- **WHEN** the new content fails validation
- **THEN** the change is NOT applied
- **AND** the previous valid configuration remains in effect
- **AND** validation errors are logged with file path and error details

#### Scenario: Config file deleted

- **GIVEN** a watched config file is being monitored
- **WHEN** the file is deleted from disk
- **THEN** the system logs a warning
- **AND** the existing runtime configuration remains in effect
- **AND** the process does NOT crash

### Requirement: Actor notification on config change

The system SHALL notify owning actors when their configuration changes via
Akka pub/sub. Each config domain SHALL map to a specific actor or service.

#### Scenario: ACL change triggers policy refresh

- **GIVEN** the ACL rules file has been validated successfully
- **WHEN** the config watcher dispatches the change event
- **THEN** the policy engine re-evaluates tool grants for active sessions

#### Scenario: Provider change triggers IChatClient rebuild

- **GIVEN** the provider configuration file has been validated successfully
- **WHEN** the config watcher dispatches the change event
- **THEN** the provider factory rebuilds `IChatClient` instances

#### Scenario: MCP profile change triggers server reconnection

- **GIVEN** an MCP server profile has been validated successfully
- **WHEN** the config watcher dispatches the change event
- **THEN** affected MCP servers are reconnected or disconnected as appropriate

#### Scenario: Schedule change triggers timer reconfiguration

- **GIVEN** the schedule definitions file has been validated successfully
- **WHEN** the config watcher dispatches the change event
- **THEN** the `ScheduleManagerActor` reconfigures timers to match the new
  definitions

### Requirement: ConfigWatcherService hosted service

The `ConfigWatcherService` SHALL be implemented as an `IHostedService` that
starts with the application and stops on shutdown. It SHALL manage
`FileSystemWatcher` instances for each watched config file.

#### Scenario: Service starts with application

- **GIVEN** the application is starting
- **WHEN** the hosted service initializes
- **THEN** `FileSystemWatcher` instances are created for each watched config
  file
- **AND** the service begins monitoring for changes

#### Scenario: Service stops on shutdown

- **GIVEN** the application is shutting down
- **WHEN** the hosted service stops
- **THEN** all `FileSystemWatcher` instances are disposed
- **AND** no further change events are processed
