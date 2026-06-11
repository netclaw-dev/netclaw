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

When a provider's authentication has been revoked or expired and the
provider exposes a verifiable token-exchange endpoint, the system SHALL
surface the failure to the operator with a specific re-authentication
remediation message and SHALL NOT silently clear or rotate the stored
credential on the operator's behalf.

#### Scenario: Missing provider credential

- **WHEN** selected provider is missing required credential fields
- **THEN** validation fails with provider-specific guidance

#### Scenario: GitHub Copilot OAuth token rejected

- **GIVEN** a `github-copilot` provider entry whose stored GitHub OAuth
  token has been revoked
- **WHEN** the system attempts to exchange the OAuth token at
  `/copilot_internal/v2/token`
- **AND** the endpoint returns `401 Unauthorized`
- **THEN** the system SHALL raise an authentication-expired error.
  Higher-level callers (the probe path, the chat-completion path) SHALL
  include the operator-chosen provider entry name when surfacing the
  failure to the operator — the name is held by the caller (it is the
  dictionary key in the `Providers` config), not by the
  `CopilotAuthExpiredException` itself
- **AND** the remediation message SHALL direct the operator to remove
  the entry (`netclaw provider remove <name>`) and re-run the device
  flow (`netclaw provider add <name> github-copilot --auth oauth-device`)
- **AND** the stored OAuth token SHALL remain in the secrets store
  unchanged so the operator retains visibility into the failing credential

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

### Requirement: GitHub Copilot provider

The system SHALL support GitHub Copilot as a selectable provider profile
identified by the type key `github-copilot`. The provider SHALL authenticate
via the GitHub OAuth device flow (RFC 8628) using
`https://github.com/login/device/code` for device authorization and
`https://github.com/login/oauth/access_token` for token exchange, requesting
the `read:user` scope. Chat completion requests SHALL route to
`https://api.githubcopilot.com/chat/completions` and model discovery to
`https://api.githubcopilot.com/models`.

The system SHALL exchange the long-lived GitHub OAuth token for a short-lived
Copilot API token by calling
`GET https://api.github.com/copilot_internal/v2/token` with header
`Authorization: token <oauth>`. The Copilot API token SHALL be cached in
memory only and never persisted to disk. The long-lived GitHub OAuth token
SHALL be persisted via the existing `ProviderEntry.OAuthAccessToken` field
in the secrets store.

Each request to `api.githubcopilot.com` SHALL carry these headers in
addition to the standard `Content-Type` and `Accept`:

- `Authorization: Bearer <copilot-api-token>`
- `copilot-integration-id: vscode-chat`
- `editor-version: <value>` (the specific value is an implementation
  detail; the header MUST be present)
- `openai-intent: conversation-agent`

Model discovery SHALL filter the `/models` response by removing entries
whose `capabilities.type` is present and not equal to `"chat"`, and by
removing entries where `model_picker_enabled` is explicitly `false`.
Entries that omit `capabilities` entirely SHALL be retained — Copilot's
`/models` payload includes shape variations across model generations,
and a missing `capabilities` block is treated as "unknown but
selectable" rather than implicitly non-chat.

#### Scenario: Operator selects GitHub Copilot in the wizard

- **GIVEN** the operator has a paid GitHub Copilot subscription
- **WHEN** they select "GitHub Copilot" in the provider picker
- **THEN** the TUI initiates a GitHub OAuth device flow showing the
  user code and verification URI
- **AND** on successful authorization the GitHub OAuth token is persisted
  to the secrets store under the operator-chosen provider name

#### Scenario: Chat completion against Copilot

- **GIVEN** a `github-copilot` provider entry is configured with a valid
  GitHub OAuth token
- **WHEN** the session actor sends a chat completion request through the
  `IChatClient` abstraction
- **THEN** the system fetches (or returns from in-memory cache) a Copilot
  API token via `/copilot_internal/v2/token`
- **AND** the outbound request goes to `api.githubcopilot.com/chat/completions`
  with `Authorization: Bearer <copilot-token>` and the
  `copilot-integration-id`, `editor-version`, and `openai-intent` headers

#### Scenario: Copilot API token refresh near expiry

- **GIVEN** a cached Copilot API token whose `ExpiresAt` is within 2 minutes
  of the current time
- **WHEN** the next chat completion request is dispatched
- **THEN** the system calls `/copilot_internal/v2/token` to obtain a fresh
  token before issuing the chat request

#### Scenario: Copilot probe lists available models

- **GIVEN** a `github-copilot` provider entry with a valid OAuth token
- **WHEN** `ProviderProbe` runs against the entry
- **THEN** the system fetches `GET https://api.githubcopilot.com/models`
  with the exchanged Copilot API token
- **AND** the returned `DiscoveredModel` list excludes entries whose
  `capabilities.type` is present and not `"chat"`, excludes entries with
  `model_picker_enabled == false`, and retains entries that omit
  `capabilities` entirely

#### Scenario: Copilot probe falls back to curated models when /models is unreachable

- **GIVEN** a `github-copilot` provider entry with a valid OAuth token
- **WHEN** `GET https://api.githubcopilot.com/models` returns a non-2xx
  status or the connection fails
- **THEN** the probe SHALL return a curated fallback list of well-known
  Copilot model IDs rather than reporting zero available models
- **AND** the probe result SHALL surface a warning indicating the fallback
  was used so operators are aware the listing is not live

### Requirement: GitHub Copilot request authorization integrity

The GitHub Copilot provider SHALL transmit the exchanged short-lived Copilot API
token on every chat completion request to `api.githubcopilot.com`. The token the
provider obtained from `/copilot_internal/v2/token` SHALL be the value present in
the outbound `Authorization: Bearer <copilot-api-token>` header as actually sent
on the wire — not a placeholder or any other credential.

Because the OpenAI SDK's own credential pipeline policy writes the
`Authorization` header from the client's `ApiKeyCredential` after any
caller-registered policy runs, the provider SHALL ensure the credential the SDK
reads carries the current Copilot token (e.g. by updating a shared mutable
`ApiKeyCredential` per request) rather than writing the `Authorization` header
directly, since a directly-written header is overwritten by the SDK and rejected
by Copilot with `HTTP 400 "Authorization header is badly formatted"`.

#### Scenario: Outbound Copilot request carries the exchanged token

- **GIVEN** a `github-copilot` provider entry with a valid GitHub OAuth token
- **AND** the token exchange returns the Copilot API token `T`
- **WHEN** a chat completion request is sent through the provider's chat client
- **THEN** the request that reaches `api.githubcopilot.com` carries
  `Authorization: Bearer T`
- **AND** the header value is NOT `Bearer placeholder` or any other credential

#### Scenario: SDK credential policy does not override the Copilot token

- **GIVEN** the OpenAI SDK is constructed with a placeholder `ApiKeyCredential`
- **WHEN** the provider issues a chat completion request
- **THEN** the SDK's credential auth policy emits the exchanged Copilot token,
  not the placeholder, because the shared credential was updated before the auth
  policy ran

