## MODIFIED Requirements

### Requirement: Controlled exposure modes

The system SHALL support explicit exposure modes with secure defaults. Startup
SHALL fail-closed when a non-local exposure mode is declared but its tunnel
infrastructure prerequisites are not met. The daemon SHALL NOT manage tunnel
processes — it validates their presence and refuses to start if they are
missing. Host-network reachable daemon access SHALL require authenticated
users. `Public` deployment posture remains a chat-audience concept and SHALL
NOT be interpreted as permission for anonymous network access. Audience types
and exposure modes are parallel controls: audience governs chat interaction,
while exposure mode governs daemon network reachability.

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

#### Scenario: Exposure mode change is audit logged

- **GIVEN** the operator changes the exposure mode in configuration
- **WHEN** the daemon restarts with the new exposure mode
- **THEN** an audit log entry records the previous and new exposure mode values
