# netclaw-slack-socket Delta Spec

## ADDED Requirements

### Requirement: Slack transport compatibility during channel seam extraction
During compatibility-first phases of the contributor-hardening program, the Slack Socket Mode transport SHALL preserve its current observable behavior while Slack is moved behind the compiled-in channel module seam.

#### Scenario: Socket Mode transport remains protected during extraction
- **WHEN** the channel module seam is introduced and valid Slack credentials are configured
- **THEN** Netclaw still opens a Slack Socket Mode connection for the protected Slack path
- **AND** the transport is not replaced by another channel mechanism during early extraction phases

#### Scenario: Protected Slack thread reply behavior remains unchanged
- **WHEN** a Slack-originated session produces a reply during an early extraction phase
- **THEN** Netclaw posts the reply into the same Slack thread that produced the session command
- **AND** the extracted channel seam does not change that protected reply behavior

### Requirement: Slack extraction fails closed on invalid seam state
If Slack's extracted channel module configuration is invalid, the system SHALL fail loudly rather than silently degrading Slack transport behavior.

#### Scenario: Invalid Slack seam state is rejected
- **WHEN** startup or hot reload encounters invalid or partial Slack channel module configuration
- **THEN** the invalid configuration is rejected with explicit diagnostics
- **AND** the system does not silently reroute Slack traffic or mask the Slack transport failure
