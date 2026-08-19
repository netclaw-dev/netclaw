## ADDED Requirements

### Requirement: Z.ai provider

The system SHALL support Z.ai as a selectable provider profile with the type key `zai`. The provider SHALL use `Microsoft.Extensions.AI.IChatClient` and the stable Z.ai OpenAI-compatible API.

The provider SHALL require an API key. It SHALL send the key with HTTP Bearer authentication and SHALL NOT offer OAuth authentication.

The default endpoint SHALL be `https://api.z.ai/api/coding/paas/v4`, the GLM Coding Plan base. Operators on the pay-as-you-go platform SHALL override `Endpoint` with `https://api.z.ai/api/paas/v4`. Chat requests SHALL use `/chat/completions`, and model discovery SHALL use `/models`.

A provider base URL with a trailing `v<digits>` path segment SHALL be treated as already versioned. Endpoint resolution SHALL NOT append another version segment to such a base.

#### Scenario: Operator adds a Z.ai provider

- **WHEN** the operator adds a `zai` provider with an API key
- **THEN** Netclaw stores the provider profile in configuration
- **AND** Netclaw stores the API key through the encrypted secrets path
- **AND** runtime resolves the provider through `IChatClient`

#### Scenario: Z.ai provider has no API key

- **GIVEN** a `zai` provider has no API key
- **WHEN** configuration validation or a provider probe runs
- **THEN** validation fails with Z.ai-specific API-key guidance
- **AND** Netclaw does not select a real chat client

#### Scenario: Z.ai model discovery

- **GIVEN** a `zai` provider has a valid API key
- **WHEN** model discovery runs
- **THEN** Netclaw calls the configured `/models` endpoint with the exact Bearer token
- **AND** Netclaw returns the model IDs from the live response

#### Scenario: Chat uses the versioned base without extra version segments

- **GIVEN** a `zai` provider uses the default `https://api.z.ai/api/coding/paas/v4` base
- **WHEN** a chat completion request is sent
- **THEN** the request URL is `https://api.z.ai/api/coding/paas/v4/chat/completions`
- **AND** the URL does not contain a second version segment

#### Scenario: Current Z.ai model capabilities

- **WHEN** discovery returns `glm-5.3`
- **THEN** Netclaw assigns a one-million-token context window
- **AND** Netclaw assigns text input and output modalities

#### Scenario: Previous Z.ai model capabilities

- **WHEN** discovery returns `glm-5.2`
- **THEN** Netclaw assigns a 200,000-token context window
- **AND** Netclaw assigns text input and output modalities

#### Scenario: Unknown Z.ai model metadata

- **WHEN** discovery returns a Z.ai model ID without documented capability metadata, such as `glm-4.6` or `glm-5-turbo`
- **THEN** Netclaw leaves its context window unresolved
- **AND** Netclaw does not invent a context value

### Requirement: Z.ai reasoning and tool-loop contract

The Z.ai provider SHALL map MEAI reasoning options to Z.ai request fields. It SHALL preserve Z.ai reasoning content when an assistant tool call returns to the provider.

The Z.ai provider SHALL NOT add Z.ai fields to generic OpenAI-compatible requests. It SHALL NOT send local-server fields to Z.ai.

#### Scenario: Disable Z.ai reasoning

- **WHEN** a request sets MEAI reasoning effort to `None`
- **THEN** the Z.ai request sets `thinking.type` to `disabled`

#### Scenario: Enable Z.ai reasoning

- **WHEN** a request sets low, medium, high, or extra-high MEAI reasoning effort
- **THEN** the Z.ai request sets `thinking.type` to `enabled`
- **AND** the request does not set a `reasoning_effort` field

#### Scenario: Replay reasoning during a tool loop

- **GIVEN** Z.ai returns reasoning content and a tool call
- **WHEN** Netclaw sends the tool result in the next request
- **THEN** the assistant history includes the returned `reasoning_content`
- **AND** the assistant history includes the original tool call

#### Scenario: Generic provider payload remains unchanged

- **WHEN** Netclaw sends a request through the generic OpenAI-compatible profile
- **THEN** it does not add Z.ai thinking or reasoning-replay fields
- **AND** it retains existing local-server request fields
