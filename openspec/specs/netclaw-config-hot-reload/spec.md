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

The system SHALL validate changed configuration before beginning runtime recovery.
Invalid configuration SHALL be rejected with logged diagnostics, and the current
daemon instance SHALL continue running with the previous effective config.
Valid configuration SHALL initiate a coordinated restart sequence instead of an
in-place actor update.

#### Scenario: Valid config change initiates coordinated restart

- **GIVEN** a watched config file changes
- **WHEN** the new content passes validation
- **THEN** the daemon closes new session ingress
- **AND** begins draining active sessions before requesting host shutdown

#### Scenario: Invalid config change rejected

- **GIVEN** a watched config file changes
- **WHEN** the new content fails validation
- **THEN** the change is NOT applied
- **AND** the current daemon instance continues running with the previous effective config
- **AND** validation errors are logged with file path and error details

#### Scenario: Config file deletion with valid resulting config initiates restart

- **GIVEN** a watched config file is being monitored
- **WHEN** the file is deleted and the resulting effective configuration remains valid
- **THEN** the daemon treats the deletion as a valid config change
- **AND** begins the same coordinated restart flow
- **AND** the process does NOT crash

### Requirement: Coordinated daemon restart on valid config change

The system SHALL coordinate valid config changes through a restart coordinator.
The coordinator SHALL capture the set of currently active sessions after ingress
is closed, wait for those sessions to drain or time out, persist restart
recovery state, and only then request daemon shutdown.

#### Scenario: Active sessions drain before restart

- **GIVEN** one or more sessions are active when a valid config change is detected
- **WHEN** restart coordination begins
- **THEN** the coordinator waits for drain completion acknowledgements from those sessions
- **AND** requests daemon shutdown only after all recorded sessions drain or the timeout expires

#### Scenario: Incoming work rejected during restart drain

- **GIVEN** the daemon has entered restart drain mode
- **WHEN** a new inbound message arrives through any daemon-managed adapter
- **THEN** the message is rejected with a restart-in-progress response
- **AND** no new session actor is created for that message

#### Scenario: Active session finishes current turn during restart drain

- **GIVEN** a session is already in `Processing` or `Compacting` when restart drain begins
- **WHEN** the coordinator requests drain for that session
- **THEN** the session rejects new work
- **AND** allows the current in-flight turn or compaction to finish
- **AND** only then passivates and acknowledges the drain

#### Scenario: Drain timeout still requests restart

- **GIVEN** at least one recorded active session does not drain before the restart timeout
- **WHEN** the timeout expires
- **THEN** the coordinator requests daemon shutdown anyway
- **AND** records that recovery will resume from the last durable checkpoint for the timed-out session

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
