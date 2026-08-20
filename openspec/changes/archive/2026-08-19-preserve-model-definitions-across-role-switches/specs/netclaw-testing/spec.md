## ADDED Requirements

### Requirement: Container upgrade compatibility proof
The smoke suite SHALL verify upgrade from the latest stable Netclaw container to a locally built image using only an isolated temporary configuration volume.

#### Scenario: Stable-to-local upgrade
- **GIVEN** the latest stable image has written or consumed a legacy config in a disposable volume
- **WHEN** a uniquely tagged local image starts against the same volume
- **THEN** the new image SHALL become healthy without modifying the file on startup
- **AND** an explicit migration SHALL preserve effective role and capability values
- **AND** switching away from and back to a definition SHALL preserve its overrides

#### Scenario: Production state isolation
- **WHEN** the upgrade smoke runs
- **THEN** it SHALL use a newly created absolute temporary directory or uniquely named test volume
- **AND** it SHALL NOT mount or inspect the default or operator-provided Netclaw home
