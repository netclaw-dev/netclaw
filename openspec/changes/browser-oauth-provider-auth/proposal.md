## Why

OpenAI's proprietary device code flow produces OAuth tokens without required API scopes (`api.model.read`, `model.request`). The provider probe fails with HTTP 403 immediately after successful OAuth login. OpenAI's device flow endpoint ignores the `scope` parameter — confirmed by testing and consistent with upstream behavior (Codex CLI has the same bug). Browser-based OAuth + PKCE is the proven fix used by OpenCode, OpenClaw, and the Codex CLI's browser path. This also unblocks TextForge MCP OAuth and any future OAuth provider. References PRD-004 (guided onboarding) and PRD-005 (multi-provider resilience).

## What Changes

- Add browser-based OAuth Authorization Code + PKCE flow for providers, starting with OpenAI.
- Extract shared OAuth primitives (PKCE generation, token exchange, token refresh) from `McpOAuthService` into a reusable service that both MCP and provider auth can use.
- Add a provider OAuth callback endpoint to the daemon's existing `:5199` HTTP server alongside the MCP OAuth callback.
- Wire `AuthMethod.OAuthPkce` (already defined in enum) into the provider descriptor and CLI/TUI flows.
- Update OpenAI descriptor to prefer browser OAuth over proprietary device flow.
- Upgrade Termina from 0.7.2 to 0.8.0 to use `CopyableTextNode` for auth URL display and `ToastOverlayNode` for clipboard feedback in the fallback UX.
- Device code flow remains available as `AuthMethod.OAuthDevice` for future providers that support it properly.

## Capabilities

### New Capabilities

- `provider-browser-oauth`: Browser-based OAuth Authorization Code + PKCE flow for provider authentication, with automatic browser launch, localhost callback, and manual URL paste fallback for headless environments.

### Modified Capabilities

- `netclaw-model-providers`: Provider auth gains browser OAuth path. `IProviderDescriptor` adds `OAuthAuthorizationEndpoint` and `OAuthRedirectUri` properties. OpenAI switches from `OAuthDevice` to `OAuthPkce` as preferred auth method.
- `netclaw-onboarding`: Init wizard and provider manager TUI gain browser OAuth sub-step with three fallback layers: auto-open browser, `CopyableTextNode` URL display with OSC 52 clipboard, and redirect URL paste-back via `TextInputNode`.

## Impact

- **Code/Runtime**: New shared OAuth service extracted from `McpOAuthService`. New daemon HTTP endpoint for provider OAuth callback. TUI changes in `InitWizardPage` and `ProviderManagerPage` for browser OAuth UX. Termina 0.8.0 upgrade.
- **Security**: OAuth tokens continue to use `secrets.json` encryption-at-rest via `SecretsFileWriter`. Localhost callback on `:5199` is already restricted to `127.0.0.1`. PKCE prevents authorization code interception.
- **Operations**: OpenAI provider setup will open a browser instead of showing a device code. Headless/SSH users paste the redirect URL. No config schema changes.
- **Dependencies**: Termina 0.8.0 (for `CopyableTextNode`, `ToastOverlayNode`, `IClipboardService`). No new external dependencies beyond what exists.
