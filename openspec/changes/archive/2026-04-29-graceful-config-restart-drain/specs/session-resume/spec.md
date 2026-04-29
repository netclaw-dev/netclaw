## ADDED Requirements

### Requirement: Warm restart recovery for previously active sessions

The daemon SHALL persist the set of sessions that were active when a
config-triggered restart began and SHALL warm that set during startup after the
actor system is available. Warmed sessions SHALL recover through the normal
journal/snapshot path without requiring an immediate client-driven `EnsureSession`
call.

#### Scenario: Previously active session warms during startup recovery

- **GIVEN** a session was recorded as active in the restart manifest before shutdown
- **WHEN** the daemon starts again after the config-triggered restart
- **THEN** startup recovery re-creates that session through the session manager
- **AND** the session rehydrates from persisted state before normal traffic resumes

#### Scenario: Previously inactive session stays cold

- **GIVEN** a session was inactive when restart drain began
- **WHEN** the daemon starts again after the config-triggered restart
- **THEN** startup recovery does NOT proactively re-create that session
- **AND** the session remains lazily recoverable on its next normal resume or input

#### Scenario: Next turn receives restart continuity notice

- **GIVEN** a session was warmed from the restart manifest
- **WHEN** the next user turn begins after the daemon restart
- **THEN** the session injects a transient restart continuity notice into the turn context
- **AND** the notice explains that recovery resumed from the last durable checkpoint
