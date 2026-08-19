## MODIFIED Requirements

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.
Supported provider type keys SHALL include `ollama`, `openai-compatible`,
`openrouter`, `openai`, `anthropic`, `github-copilot`, and `veniceai`.
All provider interactions SHALL use the Microsoft.Extensions.AI `IChatClient`
abstraction layer, ensuring provider-agnostic model access throughout the
application.

Provider model discovery SHALL extract modality metadata where the provider
API supports it. `DiscoveredModel` records SHALL include `InputModalities`
and `OutputModalities` fields populated from provider responses.

The `openai-compatible` provider SHALL support both no authentication and
API-key authentication. The API key SHALL be optional. When an API key is
configured, all OpenAI-compatible requests (chat completion, model
discovery, capability probing) SHALL send it as `Authorization: Bearer`.
When no API key is configured, requests SHALL send no authentication header.

#### Scenario: Switch provider

- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic, OpenAI, Ollama, OpenAI-compatible,
  OpenRouter, GitHub Copilot, or Venice.ai profile
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

#### Scenario: OpenAI-compatible discovery includes backend context metadata

- **GIVEN** an OpenAI-compatible provider is configured
- **WHEN** model discovery runs via `ProviderProbe`
- **THEN** the returned `DiscoveredModel` records SHALL include context-window
  metadata when the backend exposes a known field shape, including vLLM
  `max_model_len`, DwarfStar/ds4 `context_length` or
  `top_provider.context_length`, and llama.cpp `meta.n_ctx` or
  `meta.n_ctx_train`

#### Scenario: Add an OpenAI-compatible provider with an API key

- **WHEN** the operator adds an `openai-compatible` provider and supplies an
  API key through an interactive surface
- **THEN** Netclaw stores `AuthMethod: ApiKey` in the provider entry
- **AND** Netclaw stores the key through the encrypted secrets path
- **AND** chat, discovery, and probe requests send the key as
  `Authorization: Bearer`

#### Scenario: Add an OpenAI-compatible provider without an API key

- **WHEN** the operator adds an `openai-compatible` provider and supplies no
  API key
- **THEN** Netclaw stores `AuthMethod: None` and no provider secret
- **AND** chat, discovery, and probe requests send no authentication header

#### Scenario: Existing no-auth OpenAI-compatible configuration

- **GIVEN** an `openai-compatible` provider entry configured before this
  change with no API key
- **WHEN** Netclaw loads the configuration after upgrade
- **THEN** the entry behaves exactly as before the upgrade

#### Scenario: API-key auth declared without a stored key

- **GIVEN** an `openai-compatible` provider entry declares
  `AuthMethod: ApiKey` and no key is stored in secrets
- **WHEN** configuration diagnostics run
- **THEN** the failure is reported with provider-specific credential guidance
- **AND** Netclaw does not silently fall back to no-auth requests
