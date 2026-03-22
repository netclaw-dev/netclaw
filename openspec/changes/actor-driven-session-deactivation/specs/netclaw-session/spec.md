## ADDED Requirements

### Requirement: Actor-driven session activation status
The system SHALL treat a session as `active` while its `LlmSessionActor` remains live and able to accept turns, and SHALL mark the session `inactive` only when the actor deactivates itself.

#### Scenario: One subscriber disconnects from a live multi-subscriber session
- **GIVEN** a session actor has multiple subscribers attached
- **WHEN** one materialized channel pipeline disconnects
- **THEN** the remaining subscriber(s) continue receiving output
- **AND** the session status remains `active`

#### Scenario: Idle timeout deactivates a session
- **GIVEN** a session actor has no remaining subscribers
- **WHEN** its idle timeout expires and the actor passivates
- **THEN** the actor marks the session `inactive`
- **AND** the actor stops after snapshotting its recoverable state
