## ADDED Requirements

### Requirement: DeepSeek provider

The system SHALL support DeepSeek as a selectable provider profile with the type key `deepseek`. The provider SHALL use `Microsoft.Extensions.AI.IChatClient` and the stable DeepSeek OpenAI-compatible API.

The provider SHALL require an API key. It SHALL send the key with HTTP Bearer authentication and SHALL NOT offer OAuth authentication.

The default endpoint SHALL be `https://api.deepseek.com/v1`. Chat requests SHALL use `/chat/completions`, and model discovery SHALL use `/models`.

#### Scenario: Operator adds a DeepSeek provider

- **WHEN** the operator adds a `deepseek` provider with an API key
- **THEN** Netclaw stores the provider profile in configuration
- **AND** Netclaw stores the API key through the encrypted secrets path
- **AND** runtime resolves the provider through `IChatClient`

#### Scenario: DeepSeek provider has no API key

- **GIVEN** a `deepseek` provider has no API key
- **WHEN** configuration validation or a provider probe runs
- **THEN** validation fails with DeepSeek-specific API-key guidance
- **AND** Netclaw does not select a real chat client

#### Scenario: DeepSeek model discovery

- **GIVEN** a `deepseek` provider has a valid API key
- **WHEN** model discovery runs
- **THEN** Netclaw calls the configured `/models` endpoint with the exact Bearer token
- **AND** Netclaw returns the model IDs from the live response

#### Scenario: Current DeepSeek model capabilities

- **WHEN** discovery returns `deepseek-v4-flash` or `deepseek-v4-pro`
- **THEN** Netclaw assigns a one-million-token context window
- **AND** Netclaw assigns text input and output modalities

#### Scenario: Unknown DeepSeek model metadata

- **WHEN** discovery returns an unknown DeepSeek model ID without capability metadata
- **THEN** Netclaw leaves its context window unresolved
- **AND** Netclaw does not invent a context value

### Requirement: DeepSeek reasoning and tool-loop contract

The DeepSeek provider SHALL map MEAI reasoning options to DeepSeek request fields. It SHALL preserve DeepSeek reasoning content when an assistant tool call returns to the provider.

The DeepSeek provider SHALL NOT add DeepSeek fields to generic OpenAI-compatible requests. It SHALL NOT send local-server fields to DeepSeek.

#### Scenario: Disable DeepSeek reasoning

- **WHEN** a request sets MEAI reasoning effort to `None`
- **THEN** the DeepSeek request sets `thinking.type` to `disabled`

#### Scenario: Select DeepSeek reasoning effort

- **WHEN** a request sets low, medium, or high MEAI reasoning effort
- **THEN** the DeepSeek request sets `thinking.type` to `enabled`
- **AND** the request sets `reasoning_effort` to `high`

#### Scenario: Select maximum DeepSeek reasoning effort

- **WHEN** a request sets extra-high MEAI reasoning effort
- **THEN** the DeepSeek request sets `thinking.type` to `enabled`
- **AND** the request sets `reasoning_effort` to `max`

#### Scenario: Replay reasoning during a tool loop

- **GIVEN** DeepSeek returns reasoning content and a tool call
- **WHEN** Netclaw sends the tool result in the next request
- **THEN** the assistant history includes the returned `reasoning_content`
- **AND** the assistant history includes the original tool call

#### Scenario: Generic provider payload remains unchanged

- **WHEN** Netclaw sends a request through the generic OpenAI-compatible profile
- **THEN** it does not add DeepSeek thinking or reasoning-replay fields
- **AND** it retains existing local-server request fields
