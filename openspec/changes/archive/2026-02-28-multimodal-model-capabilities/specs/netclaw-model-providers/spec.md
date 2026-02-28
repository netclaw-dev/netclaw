## MODIFIED Requirements

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

## ADDED Requirements

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
