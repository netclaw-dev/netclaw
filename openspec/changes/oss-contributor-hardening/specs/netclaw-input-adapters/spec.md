# netclaw-input-adapters Delta Spec

## ADDED Requirements

### Requirement: Compiled-in channel module registry
The system SHALL register inbound and outbound channel adapters through a single compiled-in channel module registry. Generic runtime code SHALL resolve channel behavior through that registry instead of hardcoding channel-specific branches.

#### Scenario: Configured channel resolves through compiled-in module
- **WHEN** startup loads a configured channel kind that is compiled into the product
- **THEN** the channel is resolved from the single channel module registry
- **AND** inbound and outbound channel behavior is activated through the registry contract

#### Scenario: Unknown channel kind fails closed
- **WHEN** startup, doctor, or hot reload encounters a configured channel kind that is not registered in the compiled-in channel module registry
- **THEN** validation fails with channel-specific remediation
- **AND** the invalid channel configuration is not applied

#### Scenario: Dynamic channel plugin loading is rejected
- **WHEN** configuration references a runtime-discovered channel plugin or external channel assembly
- **THEN** validation fails explicitly
- **AND** the error states that MVP supports compiled-in channel modules only

### Requirement: Generic adapter runtime remains transport-agnostic
Channel modules SHALL translate inbound events into generic session commands and outbound deliveries from generic runtime broadcasts or notification requests. Session actors SHALL remain transport-agnostic during and after channel seam extraction.

#### Scenario: Extracted channel still emits generic session command
- **WHEN** an inbound message is received through a compiled-in channel module
- **THEN** the channel module emits the generic session command contract required by the runtime
- **AND** session actors do not import channel-specific transport types

### Requirement: Protected Slack compatibility during early extraction phases
During compatibility-first phases of the contributor-hardening program, the system SHALL preserve current Slack Socket Mode behavior while Slack is moved behind the channel module seam.

#### Scenario: Slack thread routing remains stable during seam extraction
- **WHEN** the channel module seam is introduced and a Slack mention arrives in a thread
- **THEN** the inbound Slack message still resolves to the `{channelId}/{threadTs}` session identity
- **AND** the message is routed through the same generic session command contract as before

#### Scenario: Slack reply delivery remains stable during seam extraction
- **WHEN** a session emits a reply originating from a Slack thread during an early extraction phase
- **THEN** the reply is delivered to the same Slack thread
- **AND** no generic notification or channel seam change alters the protected Slack reply behavior

#### Scenario: Early extraction does not silently reroute Slack traffic
- **WHEN** Slack channel configuration is invalid or incomplete during an extraction phase
- **THEN** validation fails explicitly
- **AND** the system does not silently reroute traffic to another channel or target kind
