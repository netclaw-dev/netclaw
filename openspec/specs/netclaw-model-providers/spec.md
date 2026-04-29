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
All provider interactions SHALL use the Microsoft.Extensions.AI `IChatClient`
abstraction layer, ensuring provider-agnostic model access throughout the
application.

Provider model discovery SHALL extract modality metadata where the provider
API supports it. `DiscoveredModel` records SHALL include `InputModalities`
and `OutputModalities` fields populated from provider responses.

#### Scenario: Switch provider

- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic, OpenAI, or Ollama profile
- **THEN** runtime uses selected provider through the `IChatClient` interface
  after validation

#### Scenario: Provider accessed through MEAI abstraction

- **GIVEN** a provider profile is configured
- **WHEN** the session actor sends a chat completion request
- **THEN** the request is routed through the `IChatClient` abstraction
- **AND** no provider-specific types leak into session or actor code

#### Scenario: Ollama discovery includes modality

- **GIVEN** an Ollama provider is configured
- **WHEN** model discovery runs via `ProviderProbe`
- **THEN** the returned `DiscoveredModel` records SHALL include
  `InputModalities` and `OutputModalities` populated from `/api/show`
  capability data

#### Scenario: OpenRouter discovery includes modality

- **GIVEN** an OpenRouter provider is configured
- **WHEN** model discovery runs via `ProviderProbe`
- **THEN** the returned `DiscoveredModel` records SHALL include
  `InputModalities` and `OutputModalities` populated from
  `architecture.input_modalities` and `architecture.output_modalities`

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
- **THEN** provider targets `http://my-gpu-server:11434`
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

The system SHALL expose current provider, model, and model capabilities in
diagnostics.

#### Scenario: Provider status report

- **WHEN** operator checks status
- **THEN** diagnostics include provider name, model, and last error state

#### Scenario: Model capabilities in diagnostics

- **WHEN** operator checks status
- **AND** model capabilities have been resolved
- **THEN** diagnostics SHALL include the model's input and output modalities

### Requirement: Primary and fallback model

The system SHALL support configuring both a primary model and a fallback model.
When the primary model is unavailable due to rate limiting, timeout, or error,
the system SHALL automatically switch to the fallback model. Fallback activation
SHALL be logged for operator visibility.

#### Scenario: Primary model succeeds

- **GIVEN** both primary and fallback models are configured
- **WHEN** the primary model responds successfully
- **THEN** the primary model response is used
- **AND** no fallback activation occurs

#### Scenario: Automatic fallback on primary failure

- **GIVEN** both primary and fallback models are configured
- **WHEN** the primary model returns a rate limit, timeout, or error response
- **THEN** the system retries the request using the fallback model
- **AND** a log entry records the fallback activation with the failure reason

#### Scenario: Fallback model also fails

- **GIVEN** both primary and fallback models are configured
- **WHEN** both primary and fallback models fail
- **THEN** the session receives an error indicating model unavailability
- **AND** the error is logged with details from both failures

### Requirement: Tool calling support

Tool definitions SHALL be registered through the Microsoft.Extensions.AI tool
calling API. Tool metadata SHALL be included in the system prompt so the model
is aware of available tools and their capabilities.

#### Scenario: Tools registered through MEAI API

- **GIVEN** tools are discovered from MCP servers and first-party tool providers
- **WHEN** a session is initialized
- **THEN** tool definitions are registered through the MEAI tool calling API
- **AND** the model can invoke tools through the standard MEAI tool call flow

#### Scenario: Tool metadata in system prompt

- **GIVEN** tools are registered for a session
- **WHEN** the system prompt is assembled
- **THEN** tool metadata (names, descriptions, parameter schemas) is included
  in the prompt context

### Requirement: Session config includes model capabilities

`SessionConfig` SHALL include `InputModalities` and `OutputModalities` fields
so the session actor knows what content types the configured model supports.
These fields SHALL default to `ModelModality.Text` when capabilities have not
been resolved.

#### Scenario: Session config carries modalities

- **GIVEN** model capabilities have been resolved for the configured model
- **WHEN** a new `SessionConfig` is constructed
- **THEN** `InputModalities` and `OutputModalities` SHALL reflect the
  resolved capabilities

#### Scenario: Session config defaults to text

- **GIVEN** model capabilities have not been resolved
- **WHEN** a new `SessionConfig` is constructed
- **THEN** `InputModalities` SHALL equal `ModelModality.Text`
- **AND** `OutputModalities` SHALL equal `ModelModality.Text`
