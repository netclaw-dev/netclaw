## ADDED Requirements

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

## MODIFIED Requirements

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
