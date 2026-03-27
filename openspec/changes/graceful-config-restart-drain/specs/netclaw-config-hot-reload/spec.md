## MODIFIED Requirements

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

## ADDED Requirements

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

#### Scenario: Drain timeout still requests restart

- **GIVEN** at least one recorded active session does not drain before the restart timeout
- **WHEN** the timeout expires
- **THEN** the coordinator requests daemon shutdown anyway
- **AND** records that recovery will resume from the last durable checkpoint for the timed-out session

## REMOVED Requirements

### Requirement: Actor notification on config change

**Reason**: Valid config changes now take effect through a coordinated daemon restart rather than in-place pub-sub notifications to live actors.

**Migration**: Config-domain owners should recover new effective settings during startup and session warmup instead of subscribing to direct hot-reload events.
