## ADDED Requirements

### Requirement: Socket Mode transport

Netclaw SHALL use Slack Socket Mode as the primary transport for inbound and
outbound message handling in MVP.

#### Scenario: Socket session established

- **GIVEN** valid Slack app and bot tokens are configured
- **WHEN** Netclaw starts
- **THEN** it opens a Socket Mode connection

### Requirement: No required inbound public webhook

Netclaw SHALL not require a public inbound HTTP endpoint for base Slack
transport operation.

#### Scenario: Local-only runtime

- **GIVEN** Netclaw runs with loopback-only binding
- **WHEN** Slack Socket Mode is connected
- **THEN** Slack interaction still functions
