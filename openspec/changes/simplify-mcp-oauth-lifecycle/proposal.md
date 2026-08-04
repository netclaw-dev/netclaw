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
  before atomically replacing the sole published generation; teardown is
  serialized. A replaced client is disposed once its replacement is published,
  so an invocation still in flight is cancelled rather than drained — draining
  is deferred to the separate client-lifecycle change. Strengthens PRD-006
  `MCP-009` under concurrency.
- **Failure classification**: reconnect narrows to classified
  transport/session failures and restores service for later calls without
  replaying an ambiguously failed invocation. Caller cancellation and
  tool-declared/application errors propagate without teardown or reconnect.
  Revises the reconnection behavior of PRD-006 `MCP-008` (graceful
  degradation).
- **OAuth ownership**: PKCE, authorization-code exchange, refresh, and bearer
  injection delegate to the MCP C# SDK (ModelContextProtocol.Core 1.4.1
  `ClientOAuthOptions`, `ITokenCache`, `AuthorizationCallbackHandler`).
  Netclaw's manual protocol implementation in `McpOAuthService` and the
  `mcp-oauth-metadata.json` runtime dependency are **removed**. Existing
  metadata files are ignored, not deleted.
- **Client registration stays Netclaw's**: `McpOAuthClientRegistrar` performs
  protected-resource discovery and RFC 7591 registration, because the SDK
  hard-codes `token_endpoint_auth_method: "client_secret_post"` and never reads
  the authorization server's advertised `token_endpoint_auth_methods_supported`
  (csharp-sdk#1611, unfixed in 1.4.1, every 2.0 prerelease, and `main`; PR #1615
  covers only the token request). Servers accepting public clients only — such
  as TextForge, which advertises `["none"]` — reject the SDK's registration with
  `400 invalid_client_metadata`, making SDK-driven registration impossible
  against them. Netclaw registers with exactly the method the SDK will later
  select for the token request, so the two cannot diverge, and seeds
  `ClientOAuthOptions.ClientId` so the SDK's registration path never runs. A
  missing or failing `registration_endpoint` yields an actionable error naming
  `OAuthClientId`.
- **New narrow components**: `McpOAuthFlowBroker` (interactive browser
  handoff only: opaque one-time state, bounded flow lifetime via
  `TimeProvider`) and `McpOAuthCredentialStore` (durable per-server token
  sets plus the DCR-issued client ID; supplies SDK `ITokenCache` adapters).
  One SDK authorization callback handler owns each flow's PKCE/code exchange; concurrent
  delegates observe authorization in progress but never reuse its code.
- **Durable credentials**: active token state changes only after durable
  persistence succeeds; persistence failure fails the connection visibly. A token
  response that omits `refresh_token` retains the prior refresh token. Stored
  credentials are bound to the configured MCP resource identity and withheld
  after that identity changes. Explicit authorization keeps credentials local to
  the unpublished candidate and commits them once after initialization succeeds,
  so failure cannot replace the last working record.
- **Transactional secrets**: `SecretsFileWriter` gains a path-scoped
  read/decrypt/mutate/encrypt/replace transaction so concurrent updates to
  different sections cannot lose either update. The lock is in-process and its
  key resolves a symlinked config directory, so two spellings of one file share
  it. The credential store and the TUI config/wizard save paths adopt it — the
  TUI paths because they were overwriting daemon-written `McpOAuthTokens`.
  Migrating the remaining CLI and provider callers, and any cross-process
  locking, is deferred to a separate change.
- **Callback URI**: derived from `DaemonConfig.Port`
  (`http://127.0.0.1:{port}/api/mcp/oauth/callback`) instead of hard-coded
  5199. Never derived from a request Host header.
- **Diagnostics**: authenticated OAuth API endpoints return a structured
  `McpErrorResponse`; the daemon logs full context while the client receives
  a safe actionable message. The browser callback retains safe HTML responses.
  The CLI falls back to HTTP status when the body is empty and never prints a
  blank error line. Terminal failures that occur after start are carried in the
  authenticated status response. Connection status distinguishes `AwaitingAuth`,
  `AuthFailed`, `Unreachable`, and `Connected`.

No breaking changes to the external CLI/API surface: `netclaw mcp auth`,
the OAuth start/status/callback endpoints and their response media types,
static header authentication, and unauthenticated MCP servers keep their
existing behavior.

## Capabilities

### New Capabilities

- `mcp-oauth`: OAuth authorization for HTTP MCP servers — SDK-delegated PKCE,
  exchange, and refresh with Netclaw-owned client registration, plus ownership
  boundaries, interactive browser-flow brokering, durable credential and
  client-registration persistence, callback identity, and actionable OAuth
  failure diagnostics.
- `transactional-secrets`: serialized, atomic mutation of secrets.json —
  preservation of unrelated secret sections under concurrent writers and loud
  persistence failure.

### Modified Capabilities

- `netclaw-mcp`:
  - "Configured MCP server has daemon-bound client ownership" — strengthened
    to hold under concurrent reconnects via generation-aware coalescing,
    and atomic publication.
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
  `src/Netclaw.Cli` MCP auth/status commands. Measured outcome: MCP production
  code moves from 1370 lines on `dev` (manager 618 + `McpOAuthService` 663 +
  `McpTokenCacheAdapter` 89) to 2488 (manager 1256 + credential store 604 +
  flow broker 386 + registrar 242). The increase is the diagnostics taxonomy
  (#1475), durable client identity, resource binding with legacy migration, and
  candidate-then-publish — none of which `dev` had. Net-negative production LOC
  was predicted and not achieved.
- **.NET source compatibility**: removes the public
  `McpOAuthServerMetadata` cache type and `NetclawPaths.McpOAuthMetadataPath`
  property. These represented a runtime cache that no longer exists; external
  source consumers must stop reading or constructing MCP OAuth metadata. The
  CLI and HTTP contracts remain unchanged.
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
- Legacy credential records are migrated onto the configured endpoint when they
  describe the same resource, so upgrading never invalidates a credential the
  previous release was successfully using. Equivalence tolerates a trailing
  slash, path case, and a bare-origin resource indicator; scheme, host, port,
  and query must agree, and origin-to-path narrowing is accepted while a
  path-scoped credential is never widened to a sibling path. Records that fail
  that test fail closed with actionable reauthorization guidance, and the
  rejected binding is logged next to the configured one.
- Operators see actionable status (`AwaitingAuth` → run
  `netclaw mcp auth <name>`) instead of generic connection errors.

### In scope (MVP)

- Generation-aware client lifecycle and coalesced reconnects.
- SDK-delegated OAuth with browser-flow broker and credential store.
- Transactional secrets mutation and caller migration.
- Configurable local callback port; structured diagnostics.
- Compatibility bridge: persisting the DCR client ID beside the token set and
  seeding `ClientOAuthOptions.ClientId` from it (stable SDK 1.4.1's
  `TokenContainer` carries no registration fields).
- Candidate-local credentials for explicit authorization, committed once to
  the sole durable active record immediately before successful publication.

### Out of scope

- OAuth refresh actor or proactive token-refresh timer.
- Custom bearer handler or 401 replay middleware.
- Vendored or preview SDK; the official SDK upgrade is a separate follow-up.
- Remote/public callback URL design (#297).
- Serializing all MCP tool calls.
- Draining in-flight invocations before disposing a replaced or shutting-down
  client. `dev` did not drain either, so this is a non-improvement rather than a
  regression, but it is a deliberate gap: a tool call in flight when the daemon
  stops is cancelled. Tracked with the separate client-lifecycle change.
- Migrating the remaining secrets.json callers (`SecretsCommand`, `PairCommand`,
  `ProviderCommand`, `ProviderManagerViewModel`, `ExposureModeStepViewModel`,
  `ProviderCredentialWriter`, `OAuthTokenPersistence`) and transactional
  rollback for `ProviderRenamer` and `BootstrapDeviceSeeder`.
- Attributing past provider incidents to these defects without traces.

## Source PRDs

- `docs/prd/PRD-006-mcp-tool-integration.md` — `MCP-002` (connection
  validation), `MCP-008` (graceful degradation), `MCP-009` (daemon-bound
  server ownership), and `MCP-010` (secure OAuth lifecycle).
- Evidence and design: memorizer project
  `e687a20a-21cb-4c19-8ff7-695b198df907` (audit, decision record, three
  detailed designs, delivery plan).
