## Purpose

Define a deterministic native smoke provider with a real local process and HTTP boundary.

## ADDED Requirements

### Requirement: Broad native smoke uses a deterministic loopback provider

The broad native smoke harness SHALL start a test-owned OpenAI-compatible server on loopback.
The harness SHALL wait for server health before it starts any tape or scenario.
The server SHALL expose model discovery and chat completion routes.
The server SHALL support streaming and non-streaming completions.
The server SHALL accept a tools array without tool support failure.

#### Scenario: Native smoke completes a tool-enabled first turn

- **GIVEN** the harness starts its loopback smoke provider
- **WHEN** the init wizard starts a tool-enabled first turn
- **THEN** the provider accepts the request and returns a deterministic completion
- **AND** the tape reaches the ready state

#### Scenario: Unknown model fails loudly

- **GIVEN** the harness smoke provider is active
- **WHEN** a client requests an unknown model
- **THEN** the provider returns an actionable client error
- **AND** the harness does not select another provider or model

### Requirement: Smoke provider exposure and artifacts are bounded

The smoke provider SHALL bind only to `127.0.0.1`.
The harness SHALL own its port and process lifetime.
The provider SHALL write bounded request metadata without prompt text, request bodies, or authorization values.
The harness SHALL preserve the provider log and request record after a smoke failure.

#### Scenario: Failed smoke retains safe diagnostics

- **GIVEN** a native smoke tape fails after the provider starts
- **WHEN** the harness collects artifacts
- **THEN** the provider log and request metadata are available
- **AND** neither artifact contains a prompt or authorization value

#### Scenario: Invalid loopback configuration fails

- **GIVEN** the smoke provider receives a non-loopback bind address
- **WHEN** the provider starts
- **THEN** startup fails before it accepts a request
