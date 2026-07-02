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
Supported provider type keys SHALL include `ollama`, `openai-compatible`,
`openrouter`, `openai`, `anthropic`, `github-copilot`, and `veniceai`.
All provider interactions SHALL use the Microsoft.Extensions.AI `IChatClient`
abstraction layer, ensuring provider-agnostic model access throughout the
application.

Provider model discovery SHALL extract modality metadata where the provider
API supports it. `DiscoveredModel` records SHALL include `InputModalities`
and `OutputModalities` fields populated from provider responses.

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

For GitHub Enterprise-backed Copilot entries, the provider MAY persist
non-secret host settings under `ProviderEntry.VendorOptions` as
`GitHubHost` and `GitHubApiBase`. When present, `GitHubHost` SHALL be used
to derive the device authorization endpoint at `/login/device/code` and the
OAuth token endpoint at `/login/oauth/access_token`; `GitHubApiBase` SHALL
be used to derive `/copilot_internal/v2/token`. Runtime provider resolution
SHALL use only the persisted provider entry, not ambient `GH_HOST`,
`GITHUB_API_URL`, or related GitHub environment variables, so existing public
GitHub Copilot entries keep the public endpoints unless explicitly
reconfigured. Chat completion and model discovery requests SHALL continue to
use `ProviderEntry.Endpoint`, defaulting to `https://api.githubcopilot.com`.

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

#### Scenario: Operator configures GitHub Enterprise Copilot

- **GIVEN** the operator runs `netclaw provider add <name> github-copilot --auth oauth-device --github-host <host>`
- **WHEN** OAuth authorization succeeds
- **THEN** the provider entry SHALL persist `VendorOptions.GitHubHost` and
  `VendorOptions.GitHubApiBase` when those resolved values are not the public
  GitHub defaults
- **AND** the device flow and OAuth token exchange SHALL use the resolved
  GitHub Enterprise host settings
- **AND** Copilot chat/model requests SHALL use the provider entry's
  `Endpoint` value

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

### Requirement: No-Op chat client fallback when no provider is configured

The system SHALL provide a No-Op `IChatClient` implementation that is selected
by the chat-client provider when configuration validation reports that no
valid provider/model is configured. The No-Op client SHALL allow the daemon
to start successfully in a degraded-but-operational mode rather than failing
host startup. The No-Op client SHALL NOT contact any external service and
SHALL NOT emit tool calls regardless of the tools registered on a request.

#### Scenario: Daemon starts with no provider configured

- **GIVEN** `netclaw.json` contains no inference provider/model configuration
  (fresh install, or provider section absent)
- **WHEN** the daemon starts
- **THEN** host startup SHALL succeed
- **AND** `IChatClientProvider` SHALL resolve to the No-Op client for every
  `ModelRole` (Main, Fallback, Compaction)
- **AND** a single WARN-level log entry SHALL record that the No-Op client
  was selected and reference `netclaw doctor`

#### Scenario: No-Op response contains configuration message and recovery steps

- **GIVEN** the No-Op chat client is active
- **WHEN** any caller invokes the chat client (streaming or non-streaming)
- **THEN** the response content SHALL begin with the exact phrase
  `"No valid model configuration detected."`
- **AND** the response SHALL include the recovery steps `netclaw doctor`,
  `netclaw model`, and editing `netclaw.json`
- **AND** the response SHALL NOT contain any tool calls

#### Scenario: No-Op response references available providers when discoverable

- **GIVEN** the No-Op chat client is active
- **AND** the configuration layer can enumerate known provider profiles
  without performing network I/O
- **WHEN** the No-Op client responds
- **THEN** the response SHALL include a line listing the available provider
  options
- **AND** if no provider profiles can be enumerated, the response SHALL omit
  that line rather than emit a misleading default

#### Scenario: Streaming response delivers the message as a single chunk

- **GIVEN** the No-Op chat client is active
- **WHEN** a caller invokes the streaming chat completion API
- **THEN** the No-Op client SHALL emit the configuration message as a single
  `ChatResponseUpdate` followed by a completion signal
- **AND** the No-Op client SHALL NOT simulate token-by-token streaming

### Requirement: Provider validation distinguishes "no provider configured" from "invalid"

Provider/model configuration validation SHALL produce a tri-state outcome
that the daemon composition root uses to select the chat client:
**valid**, **no provider configured** (non-fatal, selects No-Op), and
**invalid** (fatal, fails startup with the validation error). Malformed
configuration (schema violations, missing required credentials for an
explicitly configured provider, unparseable values) SHALL continue to fail
startup with the existing validation error path and SHALL NOT silently fall
back to the No-Op client.

#### Scenario: Missing provider section selects No-Op

- **GIVEN** the configuration file has no provider/model section
- **WHEN** validation runs
- **THEN** validation SHALL return the "no provider configured" outcome
- **AND** the host SHALL register the No-Op chat client

#### Scenario: Model references unknown provider selects No-Op

- **GIVEN** the configuration file declares one or more providers
- **AND** `Models:Main.Provider` references a provider name that is not in
  the providers dictionary (e.g., typo: `ollama-local1` vs `ollama-local`)
- **WHEN** validation runs
- **THEN** validation SHALL return the "no provider configured" outcome
- **AND** the No-Op banner's "Available providers:" line SHALL list the
  configured provider names so the operator can spot the typo
- **AND** the host SHALL NOT throw an unhandled exception from the
  provider plugin factory

#### Scenario: Malformed provider configuration still fails startup

- **GIVEN** the configuration file declares a provider but omits a required
  credential, contains a schema violation, or contains unparseable values
- **WHEN** validation runs
- **THEN** validation SHALL return the "invalid" outcome
- **AND** the host SHALL fail startup with the existing
  provider-specific validation error
- **AND** the host SHALL NOT fall back to the No-Op client

#### Scenario: Valid configuration uses real provider client

- **GIVEN** the configuration file declares a valid provider and model with
  all required fields present
- **WHEN** validation runs
- **THEN** validation SHALL return the "valid" outcome
- **AND** the host SHALL register the real provider's chat client through
  `NetclawChatClientProvider` (unchanged behavior)

### Requirement: Runtime status reports degraded chat client

The daemon's runtime status wire model (`DaemonRuntimeStatus.Model`) SHALL
include a `Degraded` boolean and a human-readable `DegradedReason` so that
`netclaw status` and any other consumer can render the No-Op state
distinctly. When `Degraded` is true, the overall daemon status SHALL be
reported as `degraded` rather than `healthy`, even if every other
subsystem is fine.

#### Scenario: Status reports degraded chat client and degraded overall

- **GIVEN** the daemon is running with the No-Op chat client active
- **WHEN** a client calls the runtime status endpoint
- **THEN** `Model.Degraded` SHALL be `true`
- **AND** `Model.DegradedReason` SHALL contain the validation reason
  (e.g., the configured-but-unknown provider name)
- **AND** `Overall` SHALL be `degraded`

#### Scenario: `netclaw status` renders degraded model line distinctly

- **GIVEN** the daemon reports `Model.Degraded = true`
- **WHEN** the operator runs `netclaw status`
- **THEN** the model line SHALL clearly indicate the degraded state
  (e.g., `model: (none — No-Op chat client active)`)
- **AND** the status SHALL NOT display the configured-but-broken
  `ModelId`/`Provider` as if they were a live model
- **AND** the output SHALL reference the recovery commands
  (`netclaw doctor`, `netclaw model`)

### Requirement: Chat client provider exposes degraded state for diagnostics

The `IChatClientProvider` contract SHALL expose whether it is operating in
the degraded No-Op mode so that diagnostic surfaces (notably
`netclaw doctor`) can report the state without inspecting concrete types.

#### Scenario: Doctor reports No-Op client active

- **GIVEN** the No-Op chat client is active
- **WHEN** `netclaw doctor` runs the chat-client health check
- **THEN** doctor SHALL report a **warn**-level item indicating that the
  No-Op client is active because no valid provider configuration was
  detected
- **AND** the doctor output SHALL include the recovery commands
  `netclaw model` and editing `netclaw.json`

#### Scenario: Doctor reports real client active

- **GIVEN** a real provider chat client is active
- **WHEN** `netclaw doctor` runs the chat-client health check
- **THEN** doctor SHALL report a **pass**-level item for the chat-client
  check

#### Scenario: Doctor distinguishes degraded from invalid

- **GIVEN** the daemon failed to start due to invalid provider configuration
  (and is therefore not running)
- **WHEN** `netclaw doctor` reports the chat-client check
- **THEN** doctor SHALL surface the validation **fail**-level item from the
  invalid-configuration path
- **AND** the warn-level "No-Op active" item SHALL NOT be reported in that
  case (the daemon never started)

### Requirement: Recovery requires daemon restart

The system SHALL replace the No-Op chat client with the real configured
client only on daemon restart. Hot-swapping the active chat client when
configuration becomes valid mid-process is explicitly out of scope for this
capability.

#### Scenario: Operator fixes configuration and restarts

- **GIVEN** the daemon is running with the No-Op chat client active
- **WHEN** the operator edits `netclaw.json` to add a valid provider/model
- **AND** restarts the daemon
- **THEN** validation SHALL return "valid"
- **AND** the daemon SHALL register the real provider's chat client

#### Scenario: Configuration becomes valid without restart

- **GIVEN** the daemon is running with the No-Op chat client active
- **WHEN** the operator edits `netclaw.json` to add a valid provider/model
- **AND** does NOT restart the daemon
- **THEN** the No-Op chat client SHALL remain active
- **AND** chat turns SHALL continue to return the configuration message
  until restart
