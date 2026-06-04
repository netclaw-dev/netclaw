## ADDED Requirements

### Requirement: First-launch bootstrap seeding for setup-owned non-local installs

Before the first successful daemon start in an exposure mode that requires remote authentication, the daemon SHALL auto-seed one local paired device and matching local client bearer token when the install is setup-owned and no paired devices already exist.

#### Scenario: First non-local start seeds bootstrap credential

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** no paired devices exist
- **AND** no successful non-local daemon start has been recorded yet
- **AND** the install is setup-owned
- **WHEN** the daemon starts
- **THEN** it seeds one local paired device into the paired-device registry
- **AND** it persists the matching raw token into the local client secrets store before remote-auth validation completes

#### Scenario: Existing paired device skips auto-seeding

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** one or more paired devices already exist
- **WHEN** the daemon starts
- **THEN** it does not create an additional bootstrap device automatically

#### Scenario: Successful non-local start disables future auto-seeding

- **GIVEN** the daemon previously completed a successful non-local start
- **AND** the paired-device registry is now empty
- **WHEN** the daemon starts again in `reverse-proxy` mode
- **THEN** it does not auto-seed a new bootstrap device

### Requirement: Bootstrap seeding supports manual and container first boot

Bootstrap seeding SHALL be owned by the daemon runtime rather than only by `netclaw init`, so manual config and containerized first boot receive the same first-launch behavior.

#### Scenario: Manual config first boot still seeds bootstrap credential

- **GIVEN** the operator created `netclaw.json` manually with `Daemon.ExposureMode = "reverse-proxy"`
- **AND** no wizard bootstrap files were written
- **AND** no paired devices exist
- **WHEN** the daemon starts for the first time
- **THEN** the daemon seeds the bootstrap paired device/token itself

#### Scenario: Container first boot still seeds bootstrap credential

- **GIVEN** the daemon is started in a container with persistent config storage
- **AND** `Daemon.ExposureMode` requires remote authentication
- **AND** no paired devices exist yet
- **WHEN** the containerized daemon performs its first successful startup attempt
- **THEN** the daemon seeds the same bootstrap credential state that a local manual install would receive
