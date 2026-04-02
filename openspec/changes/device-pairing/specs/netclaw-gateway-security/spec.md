## MODIFIED Requirements

### Requirement: Controlled exposure modes

The system SHALL support explicit exposure modes with secure defaults. Startup
SHALL fail-closed when a non-local exposure mode is declared but its tunnel
infrastructure prerequisites are not met. Additionally, non-local exposure
modes SHALL fail startup if no remote authentication mechanism is configured
(no paired devices and no alternative auth scheme such as OIDC).

#### Scenario: Default local mode

- **WHEN** no exposure mode is configured
- **THEN** the system binds loopback-only

#### Scenario: Internet-reachable mode requires authenticated users

- **GIVEN** exposure mode is internet-reachable (`tailscale-funnel` or
  `cloudflare-tunnel`)
- **WHEN** access policy prerequisites are missing
- **THEN** configuration validation fails
- **AND** the daemon refuses to start

#### Scenario: Non-local mode fails startup without tunnel

- **GIVEN** exposure mode is `tailscale-serve`, `tailscale-funnel`, or
  `cloudflare-tunnel`
- **WHEN** the required tunnel process (`tailscaled` or `cloudflared`) is not
  running
- **THEN** the daemon refuses to start with a descriptive error

#### Scenario: Non-local mode fails startup without remote auth

- **GIVEN** exposure mode is non-local
- **AND** no paired devices exist in the device registry
- **AND** no alternative auth scheme (OIDC/JWT) is configured
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating no remote authentication is
  available

#### Scenario: Exposure mode change is audit logged

- **GIVEN** the operator changes the exposure mode in configuration
- **WHEN** the daemon restarts with the new exposure mode
- **THEN** an audit log entry records the previous and new exposure mode values
