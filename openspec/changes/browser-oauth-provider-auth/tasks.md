## 1. Termina Upgrade

- [x] 1.1 Upgrade Termina from 0.7.2 to 0.8.0 in `Directory.Packages.props`
- [x] 1.2 Fix any breaking API changes from Termina 0.8.0 upgrade across CLI project
- [x] 1.3 Verify `dotnet build` succeeds and all existing tests pass after upgrade

## 2. Shared OAuth Primitives

- [x] 2.1 Create `OAuthPkceService` in `Netclaw.Configuration/Providers/OAuth/` with PKCE generation (code_verifier, code_challenge), authorization URL construction, and state tracking
- [x] 2.2 Add token exchange method to `OAuthPkceService` (POST authorization code + code_verifier to token endpoint, return `OAuthDeviceFlowResult`)
- [x] 2.3 Add token refresh method to `OAuthPkceService` (POST refresh_token, handle `invalid_grant`)
- [x] 2.4 Write unit tests for `OAuthPkceService` — PKCE generation, URL construction, token exchange with mock HTTP, refresh with mock HTTP
- [ ] 2.5 Refactor `McpOAuthService` to delegate PKCE generation and token exchange to `OAuthPkceService` (verify MCP OAuth still works via existing tests)

## 3. Provider Descriptor Changes

- [x] 3.1 Add `OAuthAuthorizationEndpoint` and `OAuthRedirectUri` default interface members to `IProviderDescriptor` (both `=> null`)
- [x] 3.2 Update `OpenAiDescriptor`: set `SupportedAuthMethods` to `[OAuthPkce, ApiKey]`, set `OAuthAuthorizationEndpoint` to `https://auth.openai.com/oauth/authorize`, set `OAuthRedirectUri` to `http://127.0.0.1:5199/api/provider/oauth/callback`, set `OAuthScope` to `openid profile email offline_access`

## 4. Daemon Callback Endpoints

- [x] 4.1 Add `GET /api/provider/oauth/callback` endpoint in daemon `Program.cs` — accept `code` and `state` params, validate state, exchange code for tokens, return HTML success/error page
- [x] 4.2 Add `POST /api/provider/oauth/start` endpoint — accept provider type, generate PKCE + auth URL using `OAuthPkceService`, store pending flow, return `{ authorizationUrl, state }`
- [x] 4.3 Add `GET /api/provider/oauth/status/{state}` endpoint — return `Completed`, `Pending`, or `Failed` for a pending flow
- [ ] 4.4 Write integration tests for daemon callback endpoints using `WebApplicationFactory`

## 5. CLI/TUI Browser OAuth Flow

- [x] 5.1 Add browser OAuth flow method to `ProviderManagerViewModel` — call daemon `/api/provider/oauth/start`, attempt `Process.Start(authUrl)`, poll `/api/provider/oauth/status/{state}`, handle redirect URL paste fallback
- [x] 5.2 Add browser OAuth flow method to `InitWizardViewModel` — same flow as 5.1 adapted for init wizard context
- [x] 5.3 Build browser OAuth TUI sub-step in `ProviderManagerPage` — spinner while waiting, `CopyableTextNode` for auth URL on browser failure, `TextInputNode` for redirect URL paste, `ToastOverlayNode` for clipboard feedback
- [x] 5.4 Build browser OAuth TUI sub-step in `InitWizardPage` — matching UX from `ProviderManagerPage`
- [x] 5.5 Wire auth method selection to route `OAuthPkce` to browser flow and `OAuthDevice` to existing device flow in both view models
- [x] 5.6 Add redirect URL parsing utility — extract `code` and `state` from pasted URL, validate format, return clear error on malformed input

## 6. MCP OAuth Improvements

- [ ] 6.1 Refactor `McpCommand` OAuth flow to use `OAuthPkceService` for state tracking instead of polling by server name
- [ ] 6.2 Add `CopyableTextNode` for auth URL display in MCP OAuth (replace plain text `"If it doesn't open, visit:"`)
- [ ] 6.3 Add redirect URL paste fallback `TextInputNode` to MCP OAuth flow for headless environments
- [ ] 6.4 Add `ToastOverlayNode` clipboard feedback when auth URL is copied via OSC 52
- [ ] 6.5 Refactor MCP callback endpoint to delegate token exchange to `OAuthPkceService`

## 7. Cleanup and Verification

- [x] 7.1 Update `OAuthDeviceFlowConfig.FromDescriptor` to handle `OAuthPkce` providers (skip device flow config for providers that use browser OAuth)
- [x] 7.2 Run `dotnet slopwatch analyze` — no new violations
- [x] 7.3 Run full test suite — all tests pass
- [ ] 7.4 Manual test: `netclaw provider add openai` with browser OAuth — complete flow, verify probe succeeds, verify token persisted to `secrets.json`
- [ ] 7.5 Manual test: headless fallback — paste redirect URL, verify flow completes
- [ ] 7.6 Manual test: `netclaw mcp oauth` with paste fallback — verify redirect URL paste works for MCP servers
