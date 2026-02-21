# netclaw-model-providers Specification

## Purpose

Define provider selection and validation behavior for model access.

## Requirements

### Requirement: OpenRouter default provider

The system SHALL default to OpenRouter during first-run setup.

#### Scenario: Default provider selection

- **WHEN** operator accepts defaults in onboarding
- **THEN** provider is configured as OpenRouter

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.

#### Scenario: Switch provider

- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic, OpenAI, or Ollama profile
- **THEN** runtime uses selected provider after validation

### Requirement: Optional live smoke provider checks

The system SHALL support optional provider smoke checks against a local
OpenAI-compatible endpoint such as Ollama.

#### Scenario: Ollama smoke check

- **GIVEN** operator configures Ollama endpoint
- **WHEN** smoke check is invoked explicitly
- **THEN** system reports pass or actionable failure for connectivity/auth

#### Scenario: Local dev default profile

- **GIVEN** local smoke profile is used
- **WHEN** endpoint defaults are applied
- **THEN** provider targets `http://big-gpu:11434`
- **AND** model defaults to `qwen3:30b` with fallback `qwen3:14b`

### Requirement: CI provider independence

The required automated test suite SHALL execute without live provider access.

#### Scenario: CI run without provider credentials

- **WHEN** CI runs required tests with no provider keys
- **THEN** tests pass using fakes/mocks for provider behavior

### Requirement: Provider-specific validation

The system SHALL validate required credentials and model settings per provider.

#### Scenario: Missing provider credential

- **WHEN** selected provider is missing required credential fields
- **THEN** validation fails with provider-specific guidance

### Requirement: Provider diagnostics

The system SHALL expose current provider and model in diagnostics.

#### Scenario: Provider status report

- **WHEN** operator checks status
- **THEN** diagnostics include provider name, model, and last error state
