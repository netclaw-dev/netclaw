## MODIFIED Requirements

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.
All provider interactions SHALL use the Microsoft.Extensions.AI `IChatClient`
abstraction layer, and provider onboarding SHALL include explicit auth method
branching per provider profile (`oauth-device` and/or `api-key`) where
supported.

#### Scenario: Switch provider with auth branch selection
- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic, OpenAI, or Ollama profile
- **THEN** runtime uses selected provider through the `IChatClient` interface
  after branch-specific validation
- **AND** selected auth branch is persisted with the provider profile

#### Scenario: Provider accessed through MEAI abstraction
- **GIVEN** a provider profile is configured
- **WHEN** the session actor sends a chat completion request
- **THEN** the request is routed through the `IChatClient` abstraction
- **AND** no provider-specific types leak into session or actor code

### Requirement: Provider diagnostics

The system SHALL expose current provider and model in diagnostics, including
resolved auth method, model source provenance, fallback configuration status,
and last provider error state.

#### Scenario: Provider status report includes onboarding outcomes
- **WHEN** operator checks status
- **THEN** diagnostics include provider name, auth method, primary model,
  fallback model, model source provenance, and last error state
- **AND** diagnostics mark degraded status when model source is not live catalog

## ADDED Requirements

### Requirement: OAuth device authorization support

For providers marked OAuth-capable, the system SHALL support OAuth device
authorization as a first-class provider onboarding method.

#### Scenario: OAuth device authorization succeeds
- **GIVEN** selected provider supports OAuth device flow
- **WHEN** onboarding requests a device code and the operator completes
  verification
- **THEN** system exchanges the device code for provider tokens
- **AND** provider profile is marked authorized for runtime use

#### Scenario: OAuth device polling fails
- **GIVEN** selected provider supports OAuth device flow
- **WHEN** polling returns denied, expired, or timeout states
- **THEN** onboarding reports provider-specific remediation guidance
- **AND** authorization state remains incomplete until operator retries or
  selects another auth path

### Requirement: Model discovery fallback sequence

Model selection SHALL use deterministic fallback when live model discovery is
unavailable. The fallback order SHALL be: live provider catalog, cached
last-known-good catalog, curated provider defaults, then manual model entry.

#### Scenario: Live catalog unavailable
- **GIVEN** provider profile and auth are configured
- **WHEN** live model catalog request fails
- **THEN** system attempts cached catalog and then curated defaults in order
- **AND** onboarding prompts for manual entry only if prior fallback paths fail

#### Scenario: Model provenance recorded
- **GIVEN** model is selected through any discovery path
- **WHEN** provider configuration is saved
- **THEN** system records the model source as one of `live`, `cache`,
  `defaults`, or `manual`
- **AND** diagnostics expose this provenance to operators
