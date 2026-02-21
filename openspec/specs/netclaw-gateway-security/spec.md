# netclaw-gateway-security Specification

## Purpose

Define security controls for inbound handling, exposure modes, approvals, and
audit behavior.

## Requirements

### Requirement: Default-deny policy

The system SHALL deny interactions unless explicitly allowed by ACL.

#### Scenario: Unknown sender blocked

- **WHEN** an unknown sender triggers an interaction
- **THEN** the interaction is denied

### Requirement: Fail-closed startup

The system SHALL fail startup if security-critical configuration is invalid.

#### Scenario: Invalid ACL prevents startup

- **WHEN** ACL schema is invalid
- **THEN** runtime start fails

### Requirement: Controlled exposure modes

The system SHALL support explicit exposure modes with secure defaults.

#### Scenario: Default local mode

- **WHEN** no exposure mode is configured
- **THEN** the system binds loopback-only

#### Scenario: Public mode requires auth policy

- **GIVEN** exposure mode is public (`tailscale-funnel` or
  `cloudflare-tunnel`)
- **WHEN** access policy prerequisites are missing
- **THEN** configuration validation fails

### Requirement: Privileged action approval

The system SHALL require explicit approval for privileged operations.

#### Scenario: Privileged request requires approval

- **WHEN** a privileged operation is requested
- **THEN** the system requires trusted operator approval before execution

### Requirement: Security audit visibility

The system SHALL expose policy denies and exposure status in diagnostics.

#### Scenario: Audit events visible in diagnostics

- **WHEN** policy allow/deny decisions occur
- **THEN** diagnostics include timestamped records with reason codes
