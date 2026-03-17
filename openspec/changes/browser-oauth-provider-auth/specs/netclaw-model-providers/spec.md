## MODIFIED Requirements

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.
All provider interactions SHALL use the Microsoft.Extensions.AI `IChatClient`
abstraction layer, ensuring provider-agnostic model access throughout the
application.

Provider model discovery SHALL extract modality metadata where the provider
API supports it. `DiscoveredModel` records SHALL include `InputModalities`
and `OutputModalities` fields populated from provider responses.

`IProviderDescriptor` SHALL expose `OAuthAuthorizationEndpoint` and `OAuthRedirectUri` properties (both nullable, defaulting to null) for providers that support browser-based OAuth. `OpenAiDescriptor` SHALL set `SupportedAuthMethods` to `[OAuthPkce, ApiKey]` and configure `OAuthAuthorizationEndpoint` to `https://auth.openai.com/oauth/authorize` and `OAuthRedirectUri` to `http://127.0.0.1:5199/api/provider/oauth/callback`.

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

#### Scenario: OpenAI uses browser OAuth as preferred auth method

- **GIVEN** operator selects OpenAI as provider
- **WHEN** the auth method selection is presented
- **THEN** `OAuthPkce` (browser-based) is listed as the recommended method
- **AND** `ApiKey` is listed as the fallback method
- **AND** `OAuthDevice` is not offered for OpenAI
