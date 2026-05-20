## Why

Netclaw currently supports five LLM providers (OpenAI, Anthropic, OpenRouter,
Ollama, generic OpenAI-compatible). GitHub Copilot users — who already pay
for high-quality model access via their Copilot subscription — have no way to
plug that subscription into Netclaw and must obtain a separate API key from
another provider just to run the agent. Adding Copilot as a first-class
provider removes that friction for the largest single bloc of developers with
a paid LLM subscription, and the existing `IProviderDescriptor` +
`ILlmProviderPlugin` plugin architecture (with OpenAI Codex already
demonstrating the device-flow OAuth pattern) means we can slot it in
alongside the others without framework changes.

## What Changes

- Add `github-copilot` as a new provider type registered in
  `ProviderDescriptorCatalog`, exposed in the TUI provider picker and the
  `netclaw provider add` CLI flow.
- Implement GitHub OAuth device flow (RFC 8628) for the long-lived auth
  credential, reusing the existing `OAuthDeviceFlowService` +
  `OAuthFlowCoordinator` machinery.
- Introduce a `CopilotTokenExchanger` service that swaps the GitHub OAuth
  token for a short-lived (~30 min) Copilot API token via
  `GET https://api.github.com/copilot_internal/v2/token`. The short-lived
  token SHALL be cached in-memory only and never persisted to disk; the
  long-lived GitHub OAuth token is persisted via the existing
  `ProviderEntry.OAuthAccessToken` field.
- Route Copilot chat completions to
  `https://api.githubcopilot.com/chat/completions` via an OpenAI-SDK pipeline
  policy (`CopilotRequestPolicy`) that injects the
  `copilot-integration-id`, `editor-version`, and `openai-intent` headers
  required by the API, mirroring the existing `OpenAiCodexRequestPolicy`.
- Discover available models via `GET https://api.githubcopilot.com/models`,
  filtered to `capabilities.type == "chat"` and
  `model_picker_enabled != false`. A curated fallback list is used when the
  endpoint is unreachable.
- On `401 Unauthorized` from the token-exchange endpoint, the system SHALL
  surface a re-authentication error to the operator (no silent fallback per
  the Universal Quality Bar in CLAUDE.md). The stored OAuth token MUST NOT
  be cleared automatically.
- Minor fix to `OAuthDeviceFlowService`: send `Accept: application/json` on
  both the device-authorization and token-poll POSTs. GitHub's endpoints
  return form-encoded bodies by default; the existing JSON parser fails
  without this header. Harmless for the existing OpenAI Codex flow.

Out of scope for this change:

- A Netclaw-owned GitHub OAuth app registration. Initial implementation uses
  a placeholder client ID. Production deployment will require registering a
  netclaw-owned OAuth app and updating the descriptor.
- Copilot business/enterprise endpoint differentiation
  (`api.business.githubcopilot.com`). Personal accounts only for MVP.
- Persisting the short-lived Copilot API token across restarts.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-model-providers`: add a requirement covering the GitHub Copilot
  provider — OAuth device flow + token exchange + chat call shape +
  model discovery filter — and add a scenario to "Provider-specific
  validation" covering the OAuth-expired re-auth flow.

## Impact

- **Code (new):** `src/Netclaw.Providers/GitHubCopilot/` —
  `GitHubCopilotDescriptor.cs`, `GitHubCopilotProviderPlugin.cs`,
  `CopilotTokenExchanger.cs`, `CopilotRequestPolicy.cs`,
  `CopilotAuthExpiredException.cs`.
- **Code (modified):** `ProviderDescriptorCatalog.cs`,
  `ProviderDescriptorServiceExtensions.cs`,
  `LlmProviderServiceExtensions.cs`,
  `OAuth/OAuthDeviceFlowService.cs` (add `Accept: application/json`).
- **Tests (new):** `CopilotTokenExchangerTests`,
  `GitHubCopilotDescriptorTests`; regression case in
  `OAuthDeviceFlowServiceTests` for form-encoded server responses.
- **APIs:** no public API surface changes; `IProviderDescriptor` /
  `ILlmProviderPlugin` shapes unchanged.
- **Dependencies:** none beyond the OpenAI SDK already in use.
- **Configuration / schema:** no `netclaw-config.v1.schema.json` change
  required. `ProviderEntry` already has the OAuth-token fields used for
  persistence.
- **System skills:** `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  should mention the `github-copilot` provider type and the
  `netclaw provider add <name> github-copilot --auth oauth-device` flow (per the System
  Skills Sync Rule in CLAUDE.md).
- **Security & operational impact:** the GitHub OAuth token is stored
  plaintext in the existing secrets store, same posture as other API keys.
  Short-lived Copilot tokens never touch disk. On 401 from the
  token-exchange endpoint the system fails loudly (per the no-silent-fallback
  rule) and prompts the operator to re-authenticate; it does not
  auto-clear the stored OAuth token, so operators retain visibility into
  what credential is on file.
- **PRD reference:** No specific PRD entry exists for individual provider
  additions; `docs/prd/` covers the multi-provider capability generically.
