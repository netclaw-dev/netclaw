## ADDED Requirements

### Requirement: Local control requires readable configuration

The CLI SHALL reject an unreadable local configuration source before it creates transport for a local shutdown notice or readiness probe.
This rule covers both `netclaw.json` and `secrets.json` in the canonical source chain.
The CLI SHALL report that the local daemon address is unknown.
It SHALL derive no replacement address from defaults after that read failure.
Absent optional files SHALL retain their existing default configuration semantics.
Explicit remote client endpoint selection SHALL retain its existing precedence.

An unknown address SHALL supply no HTTP proof for a stop signal.
The existing process home proof SHALL control whether the stop can proceed without that proof.
A refusal from a known endpoint SHALL retain its existing authority.
The CLI SHALL return unsuccessful readiness with no address when the local address is unknown.

#### Scenario: A valid custom address receives local control traffic

- **GIVEN** the local sources are readable and select a custom daemon port
- **WHEN** the CLI sends a local shutdown notice or readiness probe
- **THEN** it uses the canonical local address
- **AND** remote client endpoint state does not redirect that traffic

#### Scenario: An unreadable source prevents local transport

- **GIVEN** a local configuration source is malformed, inaccessible, or a directory
- **WHEN** the CLI attempts local control
- **THEN** it creates no local HTTP client and sends no request
- **AND** it reports an unknown local address

#### Scenario: A stop retains process identity proof

- **GIVEN** the local address is unknown
- **WHEN** the CLI attempts a stop
- **THEN** it signals the PID only if the existing process home rule proves that PID serves this home
- **AND** it refuses a PID that lacks that proof

#### Scenario: Unknown readiness does not report another daemon

- **GIVEN** the local address is unknown
- **WHEN** the CLI reports daemon readiness
- **THEN** it reports unsuccessful readiness and no resolved address
- **AND** it sends no readiness probe
