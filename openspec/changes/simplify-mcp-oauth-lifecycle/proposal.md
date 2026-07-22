# Proposal: Simplify MCP OAuth Ownership and Client Lifecycle

## Why

Netclaw implements the full MCP OAuth protocol (discovery, dynamic client
registration, PKCE, code exchange, refresh) in parallel with the MCP C# SDK,
which performs the same protocol again at runtime. A reliability audit
(memorizer project `e687a20a-21cb-4c19-8ff7-695b198df907`) confirmed eight
independent defects around this duplication: unserialized concurrent reconnects
that can leak or destroy the wrong client generation, a disposable
`mcp-oauth-metadata.json` cache that silently disables OAuth when absent, token
persistence that reports success on disk failure (losing rotated refresh
tokens across restart), unserialized secrets.json read-modify-write, a
hard-coded callback port, and OAuth errors that reach the CLI blank
(netclaw-dev/netclaw#1475, #1696, #297). The remediation is fewer moving
parts: one owner per concern, not a more complete second OAuth implementation.

## What Changes

- **Client lifecycle**: `McpClientManager` becomes the sole runtime owner of
  MCP client creation, publication, replacement, and disposal. Immutable
  per-server connection snapshots with monotonic generations; concurrent
  reconnects coalesce by observed generation; candidates initialize fully
  before atomically replacing the sole published generation; replaced
  generations drain in-flight calls before disposal; teardown is serialized.
  Strengthens PRD-006 `MCP-009` under concurrency.
- **Failure classification**: reconnect narrows to classified
  transport/session failures and restores service for later calls without
  replaying an ambiguously failed invocation. Caller cancellation and
  tool-declared/application errors propagate without teardown or reconnect.
  Revises the reconnection behavior of PRD-006 `MCP-008` (graceful
  degradation).
- **OAuth ownership**: OAuth discovery, DCR, PKCE, authorization-code
  exchange, refresh, and bearer injection delegate to the MCP C# SDK
  (ModelContextProtocol.Core 1.4.1 `ClientOAuthOptions`, `ITokenCache`,
  `AuthorizationRedirectDelegate`). Netclaw's manual protocol implementation
  in `McpOAuthService` and the `mcp-oauth-metadata.json` runtime dependency
  are **removed**. Existing metadata files are ignored, not deleted.
- **New narrow components**: `McpOAuthFlowBroker` (interactive browser
  handoff only: opaque one-time state, bounded flow lifetime via
  `TimeProvider`) and `McpOAuthCredentialStore` (durable per-server token
  sets plus the DCR-issued client ID; supplies SDK `ITokenCache` adapters).
- **Durable credentials**: in-memory token state publishes only after durable
  persistence succeeds; persistence failure propagates through
  `ITokenCache.StoreTokensAsync` and fails the connection visibly. A token
  response that omits `refresh_token` retains the prior refresh token. Stored
  credentials are bound to the configured MCP resource identity and withheld
  after that identity changes. Explicit authorization writes a durable pending
  record and promotes it only when the candidate connection publishes, so later
  initialization failure cannot replace the last working record.
- **Transactional secrets**: `SecretsFileWriter` gains a path-scoped,
  cross-process-locked read/decrypt/mutate/encrypt/replace transaction
  (reusing the established `WebhookRouteStore` named-mutex pattern). All
  secrets.json read-modify-write callers migrate; concurrent updates to
  different sections cannot lose either update.
- **Callback URI**: derived from `DaemonConfig.Port`
  (`http://127.0.0.1:{port}/api/mcp/oauth/callback`) instead of hard-coded
  5199. Never derived from a request Host header.
- **Diagnostics**: authenticated OAuth API endpoints return a structured
  `McpErrorResponse`; the daemon logs full context while the client receives
  a safe actionable message. The browser callback retains safe HTML responses.
  The CLI falls back to HTTP status when the body is empty and never prints a
  blank error line. Connection status distinguishes `AwaitingAuth`,
  `AuthFailed`, `Unreachable`, and `Connected`.

No breaking changes to the external CLI/API surface: `netclaw mcp auth`,
the OAuth start/status/callback endpoints and their response media types,
static header authentication, and unauthenticated MCP servers keep their
existing behavior.

## Capabilities

### New Capabilities

- `mcp-oauth`: SDK-delegated OAuth authorization for HTTP MCP servers —
  ownership boundaries, interactive browser-flow brokering, durable
  credential and client-registration persistence, callback identity, and
  actionable OAuth failure diagnostics.
- `transactional-secrets`: serialized, atomic mutation of secrets.json —
  cross-process locking, preservation of unrelated secret sections under
  concurrent writers, and loud persistence failure.

### Modified Capabilities

- `netclaw-mcp`:
  - "Configured MCP server has daemon-bound client ownership" — strengthened
    to hold under concurrent reconnects via generation-aware coalescing,
    atomic publication, and in-flight-call draining.
  - "Graceful degradation" — reconnection narrowed to classified
    transport/session failures without automatic invocation replay;
    cancellation and tool-declared errors excluded.
  - "MCP diagnostics visibility" — connection-status taxonomy
    (`AwaitingAuth`/`AuthFailed`/`Unreachable`/`Connected`) and structured
    error responses replace generic failure reporting.

## Impact

- **Code**: `src/Netclaw.Daemon/Mcp/` (`McpClientManager`, `McpOAuthService`
  — largely deleted, `McpTokenCacheAdapter`, `McpEndpointRouteBuilderExtensions`,
  `McpReconnectionService`), `src/Netclaw.Configuration/SecretsFileWriter.cs`,
  `src/Netclaw.Configuration/NetclawPaths.cs` (metadata path removal),
  `src/Netclaw.Cli` MCP auth/status commands. Expected net-negative
  production LOC.
- **Dependencies**: ModelContextProtocol.Core stays pinned at 1.4.1. A later
  official SDK release containing upstream fixes csharp-sdk#1595 (refresh
  single-flight) and csharp-sdk#1658 (cold-start client identity) is tracked
  separately and is out of scope here; the manager-level generation gate
  remains necessary regardless because SDK locks are provider-instance-scoped.
- **Issues**: fixes netclaw-dev/netclaw#1475 (blank OAuth errors); advances
  but does not close #1696 (upstream SDK fixes pending) and #297 (remote
  callback identity remains a separate product decision).
- **Docs/skills**: `netclaw-operations` system skill updated (MCP wiring and
  diagnostics change) with `metadata.version` bump; behavioral eval suite
  must pass.

### Security and operational impact

- Removes a duplicate OAuth protocol implementation that could drift from the
  SDK in standards behavior and error handling — smaller attack and defect
  surface.
- Fail-loud posture: credential persistence failures now fail the connection
  instead of silently continuing with in-memory-only rotated tokens.
- Secrets handling keeps existing encryption and file-permission hardening;
  the new transaction only adds serialization around the existing atomic
  replace.
- One-time opaque state values with bounded (five-minute) flow lifetime for
  interactive authorization; closing the browser tab cannot cancel a
  completed exchange; expired/mismatched state fails visibly. At most one
  interactive flow is active per server, and its lifetime is owned by the
  daemon rather than an HTTP request.
- Persisted OAuth credentials are bound to the configured resource identity;
  changing a profile endpoint requires authorization for the new identity.
- Legacy credential records without that binding fail closed with actionable
  reauthorization guidance rather than being silently trusted for the current
  profile. This is an operator-visible security migration, not an API shape
  change.
- Operators see actionable status (`AwaitingAuth` → run
  `netclaw mcp auth <name>`) instead of generic connection errors.

### In scope (MVP)

- Generation-aware client lifecycle, coalesced reconnects, and safe draining
  of replaced generations.
- SDK-delegated OAuth with browser-flow broker and credential store.
- Transactional secrets mutation and caller migration.
- Configurable local callback port; structured diagnostics.
- Compatibility bridge: persisting the DCR client ID beside the token set and
  seeding `ClientOAuthOptions.ClientId` from it (stable SDK 1.4.1's
  `TokenContainer` carries no registration fields).
- Durable pending credentials for explicit authorization, promoted only with
  successful candidate publication.

### Out of scope

- OAuth refresh actor or proactive token-refresh timer.
- Custom bearer handler or 401 replay middleware.
- Vendored or preview SDK; the official SDK upgrade is a separate follow-up.
- Remote/public callback URL design (#297).
- Serializing all MCP tool calls.
- Attributing past provider incidents to these defects without traces.

## Source PRDs

- `docs/prd/PRD-006-mcp-tool-integration.md` — `MCP-002` (connection
  validation), `MCP-008` (graceful degradation), `MCP-009` (daemon-bound
  server ownership), and `MCP-010` (secure OAuth lifecycle).
- Evidence and design: memorizer project
  `e687a20a-21cb-4c19-8ff7-695b198df907` (audit, decision record, three
  detailed designs, delivery plan).
