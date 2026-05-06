## MODIFIED Requirements

### Requirement: Loopback authentication scheme

The daemon SHALL register a loopback authentication scheme that automatically authenticates connections from `127.0.0.1` and `::1` as `LocalProcess` / `Operator` without requiring credentials only when the selected exposure mode allows loopback trust.

When `Daemon.ExposureMode` is `reverse-proxy`, the loopback scheme SHALL return no result for loopback requests so that only explicit credentialed schemes can authorize the connection.

#### Scenario: Loopback connection auto-authenticated in local mode

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** a connection originates from `127.0.0.1` or `::1`
- **WHEN** the client connects to `/hub/session`
- **THEN** the connection is authenticated with principal classification `Operator` and transport authenticity `LocalProcess`

#### Scenario: Non-loopback connection not auto-authenticated

- **GIVEN** a connection originates from a non-loopback address
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result (defers to other schemes)

#### Scenario: Reverse-proxy mode does not auto-authenticate loopback

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** a connection originates from `127.0.0.1`
- **WHEN** the loopback scheme evaluates the connection
- **THEN** the scheme returns no result
- **AND** the connection must authenticate through a credentialed scheme instead
