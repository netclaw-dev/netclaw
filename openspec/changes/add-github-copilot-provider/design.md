## Context

Netclaw's provider plugin system has converged on a clean three-layer shape:

1. **`IProviderDescriptor`** (in `Netclaw.Providers`, shared between CLI and
   daemon) declares the provider's identity, default endpoint, model-listing
   path, and authentication strategy (`ApiKeyAuth` / `EndpointOnlyAuth` /
   `OAuthAuth` / `MultiAuth`). Drives the TUI provider picker.
2. **`ILlmProviderPlugin`** (daemon-only, extends the descriptor) constructs
   the `Microsoft.Extensions.AI.IChatClient` instance that the session actor
   talks to.
3. **`ProviderEntry`** (in `Netclaw.Configuration`) persists the per-account
   credential and endpoint override into `secrets.json` / `netclaw.json`.

OpenAI Codex is the closest precedent for what GitHub Copilot needs: a
device-flow OAuth credential, a custom chat-backend URL, and a pipeline
policy (`OpenAiCodexRequestPolicy`) that injects per-call request metadata
into the OpenAI SDK's outgoing HTTP. Reference implementation pattern (from
`/Users/cody/code/akt-sh`) confirms the GitHub Copilot wire protocol:

- Long-lived **GitHub OAuth token** obtained via RFC 8628 device flow.
- Short-lived (~30 min) **Copilot API token** fetched on demand from
  `GET https://api.github.com/copilot_internal/v2/token` using the OAuth
  token (note: header is `Authorization: token <oauth>`, NOT `Bearer`).
- Chat completions at `https://api.githubcopilot.com/chat/completions`
  using the Copilot API token as `Authorization: Bearer <copilot>`, plus
  three mandatory custom headers: `copilot-integration-id` (server rejects
  unknown values; `vscode-chat` is the safe choice),
  `editor-version` (free-form), `openai-intent` (free-form, set to
  `conversation-agent`).

Stakeholders: operators with a paid Copilot subscription who want to use it
through Netclaw; maintainers of the provider plugin layer who will own the
new descriptor/plugin pair.

## Goals / Non-Goals

**Goals:**

- Add `github-copilot` as a first-class provider visible in the TUI picker
  and CLI provider-add flow, on parity with OpenAI/Anthropic/OpenRouter for
  the operator-facing UX.
- Reuse existing OAuth device-flow infrastructure without forking it.
- Keep the short-lived Copilot API token in memory only; persist only the
  long-lived GitHub OAuth token via the existing `ProviderEntry` fields.
- Fail loudly on auth expiry (no silent fallback per CLAUDE.md Universal
  Quality Bar).

**Non-Goals:**

- Registering a Netclaw-owned GitHub OAuth app. Initial implementation uses
  a placeholder client ID (akt-sh's `Iv1.b507a08c87ecfe98`); production
  hardening tracked separately.
- Copilot Business / Enterprise endpoint routing
  (`api.business.githubcopilot.com`). Personal accounts only.
- Persisting the Copilot API token across restarts (cheap to re-mint on
  startup; persistence would add encryption-at-rest complexity for marginal
  benefit).
- Custom `IChatClient` implementation. The OpenAI SDK's chat completions
  client handles the Copilot endpoint with no changes other than the pipeline
  policy and the dummy `ApiKeyCredential` it expects.
- Streaming-response or tool-call shape changes. Copilot's `/chat/completions`
  is OpenAI-protocol-compatible end-to-end.

## Decisions

### D1. Use the OpenAI SDK with a pipeline policy, not a custom chat client

The OpenAI SDK's `ChatClient` already speaks OpenAI's `/chat/completions`
wire format, which Copilot mirrors exactly. The only deltas are (a) a
different base URL and (b) auth header / custom-header substitution. The
`PipelinePolicy` extension point is purpose-built for this case and is
already in use for OpenAI Codex (`OpenAiCodexRequestPolicy`). A custom
`IChatClient` (like `OpenAiCompatibleChatClient`) would duplicate streaming
parsing, tool-call accumulation, and timing logic for no win.

*Alternative considered:* build a `CopilotChatClient` from scratch like
akt-sh did. Rejected — akt-sh did this because they weren't already on
`Microsoft.Extensions.AI`. We are.

### D2. CopilotTokenExchanger is a DI singleton with in-memory cache

A dedicated service owns the OAuth-token → Copilot-API-token swap. It caches
results in a `ConcurrentDictionary` and refreshes when the cached token is
within 2 minutes of expiry (matches akt-sh's behavior and gives chat calls
in flight when refresh starts a small safety margin). The cache key is the
SHA-256 of the GitHub OAuth token bytes — this gives a stable per-account
identity without needing to plumb provider names into `CreateChatClient`,
and it auto-invalidates if the operator rotates their token.

*Alternative considered:* thread the `ProviderEntry`'s logical name through
`ILlmProviderPlugin.CreateChatClient`. Rejected — would require an API
change on the plugin interface for a single provider's benefit, and the
token-hash key is just as correct.

### D3. 401 from token exchange → typed exception, do NOT auto-clear

When `/copilot_internal/v2/token` returns 401, `CopilotTokenExchanger`
throws a `CopilotAuthExpiredException`. The plugin/probe layer surfaces this
to the operator with a "run `netclaw provider fix <name>`" message. The
stored OAuth token is left in place so the operator can inspect or copy it.

CLAUDE.md is explicit: "**No silent fallbacks.** When something fails or is
misconfigured, fail loudly — do not silently degrade to a default."
akt-sh's behavior of nulling out the stored token on 401 would violate this.

### D4. ProviderEntry schema stays unchanged

`ProviderEntry.OAuthAccessToken` already exists and is the right home for
the GitHub OAuth token. `OAuthRefreshToken` and `OAuthTokenExpiry` are
unused for Copilot (GitHub OAuth tokens don't expire on a schedule; they
expire on revocation, detected lazily via the 401 path above). No
`netclaw-config.v1.schema.json` change.

### D5. Probe uses the curated fallback only on explicit failure

`GitHubCopilotDescriptor.ProbeAsync` attempts the real `/models` call first.
The curated fallback list ships only as a backstop for offline /
endpoint-down scenarios, never as the default. This matches
`OpenAiDescriptor`'s OAuth-Codex behavior where `/v1/models` is unavailable
to OAuth tokens, but inverts the priority: Copilot's `/models` DOES work
with the API token, so the live list should always win when reachable.

### D6. Send `Accept: application/json` from OAuthDeviceFlowService

GitHub's `/login/device/code` and `/login/oauth/access_token` endpoints
default to `application/x-www-form-urlencoded` response bodies and return
JSON only when the request advertises `Accept: application/json`. The
existing `OAuthDeviceFlowService` parses with `JsonDocument.Parse`, which
fails on form-encoded input. Add the header unconditionally — harmless to
OpenAI Codex's `auth.openai.com` endpoints, which already return JSON.

*Alternative considered:* fork the device-flow service per-provider.
Rejected — the header is a no-op for the existing user and is exactly
what RFC 8628 §3.4 expects.

## Risks / Trade-offs

- **[Placeholder OAuth client ID]** Using akt-sh's `Iv1.b507a08c87ecfe98`
  in checked-in source ties Netclaw users to a third-party OAuth app
  registration that we don't control. → **Mitigation:** the descriptor reads
  the client ID from a constant; flip to a netclaw-owned client ID before
  the first release that ships the provider as supported (not experimental).
  Document in proposal's "out of scope" section.

- **[Telemetry header values]** The Copilot API rejects unknown
  `copilot-integration-id` values. We hardcode `vscode-chat`, which is
  technically a misrepresentation of the calling client. → **Mitigation:**
  if the API later restricts this, we can register a Netclaw integration ID
  with GitHub. No user impact today.

- **[Token-hash cache key collides across processes]** A second Netclaw
  process started with the same secrets file will pay an unnecessary token
  exchange on first chat. → **Mitigation:** acceptable; the exchange is
  cheap (single HTTP round-trip) and the cache amortizes across the
  remaining ~30 minutes of token life.

- **[Curated fallback drift]** Copilot's model lineup evolves; a stale
  curated list could let the operator pick a model the API later 404s on.
  → **Mitigation:** curated list is only used when `/models` is unreachable.
  In that state, the operator already knows the provider is impaired.

- **[Akt-sh as reference implementation]** Akt-sh is a working third-party
  app, not an official GitHub integration. Wire protocol details could
  change without notice. → **Mitigation:** the probe path
  (`ProviderDescriptorRegistry.ProbeAsync`, invoked by the TUI/CLI add
  flow) exercises both token exchange and model listing on every probe,
  so silent drift surfaces quickly. Failure messages include the
  offending endpoint URL so debugging is direct.

## Migration Plan

No data migration required. New provider; no existing configs reference it.
Operators who want to switch to Copilot run `netclaw provider add <name>
github-copilot --auth oauth-device`, which triggers the device flow and
persists the GitHub OAuth token alongside other secrets. Rollback:
`netclaw provider remove <name>` clears the entry.

## Open Questions

- Should the `editor-version` header carry `Netclaw/<version>` or the
  akt-sh `Neovim/0.6.1` value? The Copilot API has been observed to reject
  some editor-version values; verify during implementation by probing with
  `Netclaw/<version>` first and falling back to `Neovim/0.6.1` (with a
  code comment) only if necessary.
- Does the Copilot `/models` endpoint return modality metadata
  (`input_modalities` / `output_modalities`) that `netclaw-model-providers`'
  modality requirement expects? If not, populate
  `DiscoveredModel.InputModalities` / `OutputModalities` to
  `ModelModality.Text` defaults; revisit if Copilot exposes image-capable
  models with appropriate metadata.
- Does anything in the daemon assume a single global `HttpClient`-per-
  provider? `CopilotTokenExchanger` and `GitHubCopilotDescriptor` both
  need HTTP; ensure they don't both grab their own `HttpClient` instance
  in a way that breaks named-client conventions used elsewhere in
  `Netclaw.Providers`.
