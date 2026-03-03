## MODIFIED Requirements

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.
All provider interactions SHALL use the Microsoft.Extensions.AI `IChatClient`
abstraction layer, ensuring provider-agnostic model access throughout the
application.

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

## ADDED Requirements

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
