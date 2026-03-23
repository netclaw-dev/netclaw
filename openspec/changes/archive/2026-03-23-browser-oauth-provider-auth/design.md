## Context

OpenAI provider auth uses a proprietary device code flow (`OpenAiDeviceFlowService`) that produces tokens without API scopes. The device flow endpoint ignores the `scope` parameter, so tokens lack `api.model.read` and `model.request` — the provider probe fails with HTTP 403.

The daemon already has browser-based OAuth + PKCE infrastructure for MCP servers (`McpOAuthService` on `:5199`), including PKCE generation, callback handling, token exchange, and token refresh. The core OAuth primitives are sound but coupled to MCP-specific concerns (RFC 7591 dynamic client registration, resource indicators, MCP server metadata discovery).

Termina 0.8.0 adds `CopyableTextNode` with OSC 52 clipboard support and `ToastOverlayNode` for non-blocking notifications — enabling the fallback UX for headless environments.

## Goals / Non-Goals

**Goals:**
- Replace OpenAI's proprietary device flow with browser-based OAuth + PKCE that requests proper scopes.
- Extract shared OAuth primitives from `McpOAuthService` into a reusable service.
- Add provider OAuth callback endpoint to the daemon.
- Wire browser OAuth into init wizard and provider manager TUI with three fallback layers.
- Upgrade Termina to 0.8.0 for clipboard and toast support.

**Non-Goals:**
- Remove device code flow entirely — it remains for future providers that support it properly.
- Change MCP OAuth behavior — `McpOAuthService` delegates to shared primitives but retains its MCP-specific discovery and registration logic.
- Add OAuth for Anthropic in this change — Anthropic's OAuth situation is separate.
- Implement full text selection in Termina — `CopyableTextNode` from 0.8.0 is sufficient.

## Decisions

### Decision: Extract shared OAuth primitives into `OAuthPkceService`

Create a new `OAuthPkceService` in `Netclaw.Configuration` that owns the reusable OAuth primitives:
- PKCE code_verifier/code_challenge generation (extracted from `McpOAuthService` lines 400-418)
- Authorization URL construction with scope, state, PKCE challenge
- Authorization code → token exchange via POST to token endpoint
- Refresh token exchange
- State tracking for pending flows (`ConcurrentDictionary<state, pending>`)

`McpOAuthService` delegates to `OAuthPkceService` for these operations while retaining MCP-specific logic (server discovery, dynamic client registration, resource indicators).

Rationale: Avoids duplicating PKCE/token logic. Single place to fix OAuth bugs. Provider and MCP auth share the same battle-tested token exchange code.

Alternatives considered:
- Duplicate PKCE logic in a new provider-specific service. Rejected: maintenance burden, divergence risk.
- Make `McpOAuthService` generic enough for providers. Rejected: MCP discovery is deeply embedded, would be a large refactor with high regression risk.

### Decision: Provider OAuth callback shares daemon HTTP server on `:5199`

Add a new endpoint to the daemon's existing ASP.NET Core host:

```
GET  /api/provider/oauth/callback?code=X&state=Y
POST /api/provider/oauth/start
GET  /api/provider/oauth/status/{state}
```

The CLI calls `/api/provider/oauth/start` to initiate the flow (daemon generates PKCE, builds auth URL, stores pending state). The daemon receives the callback and exchanges the code for tokens. The CLI polls `/api/provider/oauth/status/{state}` until complete.

Redirect URI: `http://127.0.0.1:5199/api/provider/oauth/callback`

Rationale: Reuses existing HTTP infrastructure. No new ports. Callback arrives at the daemon which can immediately exchange the code (no race with CLI process lifecycle).

Alternatives considered:
- CLI runs its own callback server. Rejected: CLI might exit before callback arrives, port conflicts if daemon also listens.
- Use a fixed port different from `:5199`. Rejected: unnecessary complexity, single port is cleaner.

### Decision: Three-layer fallback UX

```
Layer 1: Process.Start(authUrl)
  → Browser opens, user authorizes, callback received. Done.

Layer 2: Browser fails → display URL via CopyableTextNode
  → OSC 52 copies to clipboard, toast confirms.
  → User opens URL manually, callback received. Done.

Layer 3: Callback unreachable (SSH/firewall)
  → TextInputNode: "Paste the redirect URL"
  → CLI extracts code+state from pasted URL, sends to daemon.
```

Rationale: Covers desktop (layer 1), SSH-with-browser-elsewhere (layer 2), and air-gapped (layer 3). Matches the pattern used by Claude Code, `gh auth login`, and OpenCode.

### Decision: OpenAI switches to `OAuthPkce` as preferred auth method

`OpenAiDescriptor.SupportedAuthMethods` changes from `[OAuthDevice, ApiKey]` to `[OAuthPkce, ApiKey]`. The proprietary device flow service remains in the codebase but OpenAI no longer uses it.

Authorization endpoint: `https://auth.openai.com/oauth/authorize`
Token endpoint: `https://auth.openai.com/oauth/token` (already configured)
Client ID: `app_EMoamEEZ73f0CkXaXp7hrann` (already configured)
Scopes: `openid profile email offline_access` (identity scopes only — browser flow grants API access implicitly, matching OpenCode's behavior)

Rationale: Browser flow works. Device flow doesn't. OpenCode proves identity scopes are sufficient via browser flow.

### Decision: Add `OAuthAuthorizationEndpoint` and `OAuthRedirectUri` to `IProviderDescriptor`

Two new default interface members:

```csharp
string? OAuthAuthorizationEndpoint => null;
string? OAuthRedirectUri => null;
```

`OpenAiDescriptor` sets:
```csharp
OAuthAuthorizationEndpoint => "https://auth.openai.com/oauth/authorize"
OAuthRedirectUri => "http://127.0.0.1:5199/api/provider/oauth/callback"
```

Other descriptors inherit `null` defaults — zero changes needed.

## Risks / Trade-offs

- [Browser may not open on headless servers] → Mitigation: three-layer fallback covers this. `Process.Start` failure is caught and handled gracefully.
- [OSC 52 not supported by all terminals] → Mitigation: best-effort clipboard copy. URL is always displayed as text regardless. Toast shows "Copied" only when OSC 52 is emitted (no way to detect if terminal actually accepted it).
- [Port 5199 blocked or occupied] → Mitigation: existing risk for MCP OAuth too. Daemon startup already validates the port. Error message guides user to check port availability.
- [Redirect URL paste parsing] → Mitigation: parse with `Uri` class, extract `code` and `state` query parameters. Validate state matches pending flow. Clear error on malformed URLs.
- [Token refresh with browser OAuth tokens] → Mitigation: `offline_access` scope ensures refresh tokens are issued. Existing `OAuthTokenPersistence` handles storage. Refresh logic extracted into shared `OAuthPkceService`.

## Migration Plan

1. Upgrade Termina from 0.7.2 to 0.8.0 in `Directory.Packages.props`.
2. Extract `OAuthPkceService` from `McpOAuthService` primitives.
3. Refactor `McpOAuthService` to delegate to `OAuthPkceService`.
4. Add provider OAuth callback endpoints to daemon `Program.cs`.
5. Add `OAuthAuthorizationEndpoint` and `OAuthRedirectUri` to `IProviderDescriptor`.
6. Update `OpenAiDescriptor` to use `OAuthPkce` with browser flow endpoints.
7. Wire browser OAuth flow into `ProviderManagerViewModel` and `InitWizardViewModel`.
8. Build browser OAuth TUI sub-step in `InitWizardPage` and `ProviderManagerPage` using `CopyableTextNode` and `ToastOverlayNode`.
9. Update existing tests, add new tests for `OAuthPkceService` and callback endpoints.

Rollback: Revert `OpenAiDescriptor.SupportedAuthMethods` to `[OAuthDevice, ApiKey]`. Shared service remains harmless.
