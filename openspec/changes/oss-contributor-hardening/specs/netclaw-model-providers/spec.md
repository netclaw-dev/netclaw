# netclaw-model-providers Delta Spec

## ADDED Requirements

### Requirement: Compiled-in provider module registry
The system SHALL register inference providers through a single compiled-in provider module registry. Provider selection, validation, model discovery, and runtime client construction SHALL resolve through this registry rather than through scattered provider-specific branching in generic runtime code.

#### Scenario: Configured provider resolves through compiled-in module
- **WHEN** startup loads a configured provider kind that is compiled into the product
- **THEN** the provider is resolved from the single provider module registry
- **AND** generic runtime code consumes the provider through the registry contract rather than provider-specific conditionals

#### Scenario: Unknown provider kind fails closed
- **WHEN** startup or doctor encounters a configured provider kind that is not registered in the compiled-in provider module registry
- **THEN** validation fails with provider-specific remediation
- **AND** runtime startup is blocked for that configuration

#### Scenario: Dynamic provider plugin loading is rejected
- **WHEN** configuration references a runtime-discovered provider plugin or external provider assembly
- **THEN** validation fails explicitly
- **AND** the error states that MVP supports compiled-in provider modules only

### Requirement: Provider module seam preserves provider-agnostic runtime contracts
Provider modules SHALL terminate at the provider/runtime boundary. Session actors and shared runtime flows SHALL continue to depend on provider-agnostic runtime client contracts and SHALL NOT import provider-specific SDK types.

#### Scenario: Session runtime remains provider-agnostic after seam extraction
- **WHEN** a session turn is executed using any configured provider module
- **THEN** the session runtime invokes the provider through the shared runtime client contract
- **AND** no provider-specific type crosses into actor-facing contracts

### Requirement: Protected OpenAI compatibility during early extraction phases
During compatibility-first phases of the contributor-hardening program, the system SHALL preserve the current OpenAI API-key inference path and the current OpenAI OAuth/subscription path while provider seam extraction is in progress.

#### Scenario: OpenAI API-key inference path remains behaviorally stable
- **WHEN** the provider module seam is introduced and OpenAI is configured for API-key authentication
- **THEN** model validation, runtime client construction, and inference requests continue to succeed through the OpenAI path
- **AND** no new contributor-facing migration step is required for that protected path

#### Scenario: OpenAI OAuth or subscription path remains behaviorally stable
- **WHEN** the provider module seam is introduced and OpenAI is configured for OAuth or subscription-backed authentication
- **THEN** the OpenAI runtime path continues to acquire usable runtime credentials through the protected compatibility flow
- **AND** the runtime behavior remains equivalent to the pre-extraction path for successful configurations

#### Scenario: Early extraction does not silently downgrade OpenAI auth mode
- **WHEN** a protected OpenAI path is partially configured or invalid during an early extraction phase
- **THEN** validation fails with an explicit auth-specific error
- **AND** the system does not silently switch between API-key and OAuth/subscription modes
