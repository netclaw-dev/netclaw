## Context

Netclaw runs the full MCP OAuth 2.1 protocol twice. `McpOAuthService` performs
protected-resource and authorization-server discovery, `WWW-Authenticate`
parsing, dynamic client registration (DCR), PKCE URL construction,
authorization-code exchange, refresh-token redemption, and metadata caching.
At the same moment `McpClientManager.BuildOAuthOptions` hands the MCP C# SDK a
`ClientOAuthOptions` with an `ITokenCache`, so the SDK performs the same
protocol again inside its transport. Two implementations of one standard can
drift on provider quirks, error handling, and credential state.

A reliability audit (memorizer project `e687a20a-21cb-4c19-8ff7-695b198df907`)
confirmed eight independent defects around this duplication:

1. Concurrent reconnects race. `TryReconnectAsync` removes and disposes the
   live client, then rebuilds it with no per-server coordination. It is
   reachable from missing-tool recovery, invocation-exception recovery, the
   OAuth callback, and the background reconnection service, so two failures can
   tear down the same generation, leak the losing candidate, or let a failed
   rollback delete another caller's healthy client.
2. Cancellation and broad retry are unsafe. `InvokeSharedAsync` catches every
   exception, tears the shared client down, reconnects, and retries — so caller
   cancellation can destroy a healthy client, and a retry can duplicate a tool
   whose remote side effect already succeeded.
3. OAuth wiring depends on a disposable cache. `CreateClientAsync` skips
   discovery when a token exists and `BuildOAuthOptions` returns `null` when
   `mcp-oauth-metadata.json` is absent and no static client ID is set, so a
   valid token plus a missing metadata file silently installs no OAuth at all.
4. Token persistence reports false success. `McpTokenCacheAdapter` publishes the
   rotated token in memory and calls `PersistTokens`, which catches every
   exception and only logs. The SDK believes a rotated refresh token was stored;
   the next restart reloads a stale, potentially single-use token and gets
   `invalid_grant`.
5. Atomic replacement is not serialized mutation. `SecretsFileWriter` replaces
   the file atomically but does not lock the surrounding read/modify/write, so
   concurrent writers to different sections can each overwrite the other.
6. Netclaw duplicates the SDK OAuth protocol (the root cause above).
7. The callback URI is hard-coded to `http://127.0.0.1:5199/...` in three
   places even though `DaemonConfig.Port` is configurable.
8. Diagnostics lose actionable errors. A provider that advertises DCR but
   rejects registration with a bodyless `403` reaches the CLI as a blank error
   line (netclaw-dev/netclaw#1475).

The remediation is fewer moving parts, not a more complete second OAuth stack:
one owner per concern. `ModelContextProtocol.Core` stays pinned at **1.4.1** —
the stable release Netclaw already ships — so the design must work within that
version's surface and its known gaps.

This subsystem is not actor-hosted. `McpClientManager` is an `IHostedService`;
tool calls reach it through `IMcpToolInvoker`, not a mailbox. The only durable
state is `secrets.json`; no Akka.Persistence journal or snapshot is involved.
The design keeps those boundaries and hardens the two responsibilities Netclaw
cannot delegate: client lifecycle and durable local state.

## Goals / Non-Goals

**Goals:**

- Make `McpClientManager` the sole runtime owner of MCP client creation,
  publication, replacement, and disposal, holding one published generation per
  server under concurrent reconnects while safely draining replaced generations.
- Coalesce concurrent reconnects by observed generation; publish a fully
  initialized candidate atomically; serialize teardown.
- Narrow reconnect to classified transport/session failures, restore service
  for later calls without replaying an ambiguously failed invocation, and
  propagate cancellation and application/tool errors untouched.
- Delegate the whole OAuth protocol to the MCP C# SDK and delete Netclaw's
  parallel implementation and its `mcp-oauth-metadata.json` runtime dependency.
- Persist per-server token sets plus the DCR-issued client ID durably before
  publishing them in memory; bind them to the configured MCP resource identity;
  fail the connection loudly on persistence failure or binding mismatch.
- Serialize `secrets.json` read/modify/write with a path-scoped lock so
  concurrent writers to different sections cannot lose each other's update.
- Derive the callback URI from `DaemonConfig.Port`.
- Return structured, actionable OAuth diagnostics; never print a blank error.

**Non-Goals:**

- An OAuth refresh actor or a proactive token-refresh timer.
- A custom bearer handler or 401 replay middleware.
- Vendoring or previewing a patched SDK; the official upgrade is a later PR.
- Remote/public callback URL design (netclaw-dev/netclaw#297).
- Serializing every MCP tool call.
- Attributing past provider incidents to these defects without request traces.

## Decisions

### D1. The SDK owns the OAuth protocol; Netclaw stops re-implementing it

**Decision:** Authorization-server selection, PKCE, authorization-code exchange,
bearer injection, and refresh delegate entirely to the SDK via
`ClientOAuthOptions`, `ITokenCache`, and `AuthorizationRedirectDelegate`.
`McpOAuthService`'s protocol code — authorization-URL construction, code
exchange, refresh redemption, `GetValidTokenAsync`, and the metadata cache — is
deleted.

**Amended during implementation:** DCR does *not* delegate to the SDK.
`ClientOAuthProvider.PerformDynamicClientRegistrationAsync` hard-codes
`TokenEndpointAuthMethod = "client_secret_post"` and reads
`token_endpoint_auth_methods_supported` only afterwards, for the token request.
Against a server advertising `["none"]` the registration is rejected with
`400 invalid_client_metadata`, and 1.4.1 exposes no hook to change the body —
`DynamicClientRegistrationOptions` has four properties and its `ResponseDelegate`
fires only on success. This is csharp-sdk#1611, open since 2026-05-29 and unfixed
in 1.4.1, every 2.0 prerelease, and `main`; PR #1615 addresses only the token
request. `dev`'s own 40-line registration sent `"none"` and worked, as does the
TypeScript SDK, which negotiates from the advertised list. So
`McpOAuthClientRegistrar` retains protected-resource discovery and registration,
registering with the same method the SDK will later select, and seeds
`ClientOAuthOptions.ClientId` so the SDK's registration path never runs.

**Rationale:** One protocol implementation cannot drift from itself. 1.4.1
already exposes every hook the interactive and non-interactive flows need, and
the redirect delegate explicitly supports custom UI, so the manual stack can go
now — before the pending upstream fixes ship. This is a net-negative-LOC change
that shrinks the attack and defect surface.

**Alternatives considered:**

- Finish Netclaw's own OAuth implementation to close the eight defects in place.
  Rejected: it keeps two protocol authorities that can disagree on standards
  behavior, and it grows the exact surface the audit says to remove. Fixing
  bugs inside a duplicate is more code and more drift, not less.

### D2. One generation-aware lifecycle gate and one published generation per server

**Decision:** Replace the independent client/tool dictionaries with one
immutable per-server connection snapshot — `McpClient`, its function map, a
monotonically increasing generation, and status metadata. A per-server async
lifecycle gate guards create/publish/replace/teardown. A reconnect captures the
generation it observed, enters the gate, re-reads the published snapshot, and if
a newer healthy generation already exists it reuses that and returns — so
concurrent reconnects coalesce onto one winner. Otherwise it builds a candidate
**without** removing the published connection, initializes it fully
(`ListToolsAsync` and function-map construction), publishes it atomically as the
next generation, updates status and `ToolRegistry` from that same snapshot, then
retires the replaced generation. Each invocation acquires a lease on the
snapshot it uses; retirement blocks new leases but disposes the generation only
after its final in-flight invocation releases its lease. Initialization failure
disposes only the candidate and keeps the published connection.

Invocation narrows accordingly: tool-declared MCP errors stay results and are
formatted, never reconnected; caller-token `OperationCanceledException` and
unknown application exceptions propagate with no teardown; only classified
transport/session failures request one coalesced replacement for later calls.
The failed invocation returns an error without automatic replay because the
remote operation may have completed before the transport failure became
visible. A failed invocation releases its snapshot lease before it requests or
awaits reconnect, so replacement retirement can never wait on the reconnecting
caller's own lease.

**Rationale:** The gate protects lifecycle transitions, not OAuth. Coalescing on
generation gives ten simultaneous reconnects exactly one candidate and one
published survivor, which a plain lock cannot. Building the candidate before
retiring the incumbent means a failed replacement is invisible to callers;
leasing prevents routine replacement from interrupting work already in flight.

**Alternatives considered:**

- A plain `SemaphoreSlim` that tears down the client, then reconnects each
  waiter in turn. Rejected: it serializes the race without coalescing it —
  every waiter still reconnects, producing N candidates, N provider instances,
  and the same leak/overwrite the audit found, just one at a time.

### D3. No OAuth actor, no refresh service, no bearer middleware

**Decision:** Keep this subsystem out of the actor system. `McpClientManager`
stays an `IHostedService`; the new flow broker and credential store are plain
singletons; the lifecycle gate is an async gate over a generation counter. No
proactive refresh timer or actor, and no custom bearer/401-replay handler.

**Rationale (from the decision record):** An actor earns its keep only when its
mailbox owns the full mutable state and every relevant operation passes through
it. A credential-only actor would not coordinate the `ClientOAuthProvider`
instances created by concurrent reconnects, the SDK refreshes that happen inside
transport requests, client publication and teardown, or external `secrets.json`
writers — so it would not close the races that matter. Routing every MCP call
through an actor would coordinate them, but at the cost of a new invocation hop,
response adaptation, cancellation and mailbox-throughput semantics, and shutdown
behavior — for a boundary the manager already owns. A generation-aware async
gate is the smaller mechanism. A Netclaw refresh timer would be a second refresh
authority beside the SDK: the two could redeem the same rotating refresh token
concurrently, and it would drag provider-specific token-endpoint behavior back
into Netclaw. If idle-grant keepalive ever becomes a requirement, it should call
an MCP-level operation through the SDK, not redeem a refresh token directly.

### D4. Transactional mutation extends `SecretsFileWriter`; no OAuth-specific store

**Decision:** Add a path-scoped transactional update to `SecretsFileWriter` that
derives a lock identity from the canonical path, then holds one lock across
read, leaf decryption, JSON parse, the caller's mutation, serialization,
encryption, and `AtomicFile` replacement with permission hardening.

**Amended during implementation:** the lock is in-process rather than a named
cross-process mutex, and its key resolves only the immediate parent directory's
symlink rather than every path segment. Nothing enforces that the CLI writes only
while the daemon is stopped, so a `netclaw secrets set` issued against a live
daemon can still lose against a concurrent token refresh — the same hazard as
before this change, when every caller performed an unlocked read-modify-write.
Closing it needs a cross-process lock *and* the remaining callers moved onto the
transaction; both are deferred together rather than half-done. Caller migration is limited to the credential
store and the two TUI save paths that were overwriting daemon-written
`McpOAuthTokens`; the remaining CLI and provider callers, and transactional
rollback for `ProviderRenamer` and `BootstrapDeviceSeeder`, move to their own
change. Callers pass
owned field mutations that execute against the latest document read under the
lock; they do not pass a whole-file snapshot captured before the lock. Long-lived
editors retain their contribution actions and replay those actions inside the
transaction. Whole-file `Write` stays for intentional replacement but
participates in the same path lock. One singleton credential store backs every
per-connection `ITokenCache` adapter.

**Rationale:** Reuse before add. `SecretsFileWriter` already owns encryption,
owner-only permissions, and atomic replace; the gap is only serialization around
the read/modify/write, which the webhook store already solved for the same
file-per-key shape. A separate OAuth file format would be a parallel construct
that duplicates encryption and permission logic and drifts from it. A shared
singleton store also stops per-adapter in-memory dictionaries from diverging.

**Alternatives considered:**

- A dedicated OAuth credential file with its own writer and lock. Rejected: it
  reinvents the encryption, hardening, and atomic-replace machinery
  `SecretsFileWriter` already provides, for no benefit, and adds a second
  secret-bearing file to protect.

### D5. A temporary client-ID bridge and fail-closed resource binding for stable SDK 1.4.1

**Decision:** Store the effective DCR-issued client ID — and client secret,
when the provider issues one — in the durable token record and seed
`ClientOAuthOptions.ClientId`/`ClientSecret` from it on restart. A
per-connection OAuth context initializes its client ID from
`McpServerEntry.OAuthClientId` or the stored record, updates it from
`DynamicClientRegistration.ResponseDelegate`, and has `StoreTokensAsync` persist
the `TokenContainer` plus that effective client identity together.

The active durable record and any pending replacement record store the configured
resource identity that received the credentials. Its canonical representation is the absolute configured MCP
endpoint URI after `System.Uri` normalization, with scheme and host normalized,
default port normalized, fragment removed, and path and query retained. Before
an `ITokenCache` adapter returns credentials or the manager seeds SDK options,
the per-connection context compares the current endpoint identity with the
stored binding. A mismatch or missing legacy binding returns no tokens and does
not seed a dynamically registered client ID or client secret. It reports
`AwaitingAuth` and leaves the old durable record intact until a new authorization
succeeds. An operator-configured static client ID remains authoritative because
it belongs to current configuration rather than the stored dynamic registration.
This check happens before the SDK can inject an old bearer token into a request
to the changed endpoint. Netclaw does not silently stamp a binding onto a legacy
record because the profile may have changed before upgrade.

An explicit authorization candidate uses a flow-scoped token cache that starts
without an access token. SDK stores remain local to that unpublished candidate.
After tool listing succeeds, the lifecycle gate writes the complete replacement
to the sole durable active record, transfers cache ownership, and publishes the
candidate. Candidate failure or expiry discards the local cache without a durable
cleanup path. A crash after the durable write but before runtime publication
leaves a complete active record for the next startup. Ordinary refresh on a
published connection continues to update the active record persist-before-return.

If an authorization server rejects a stored dynamically registered identity as
`invalid_client`, the current flow fails visibly and the rejected client
identity is discarded from the durable record while its tokens are left intact.
The next explicit authorization registers a new client, because an absent client
ID is already the registrar's trigger.

**Amended during implementation:** an earlier design persisted a
`RejectedDynamicClientId` marker instead of discarding the identity. That is a
latch: it survives restart, has to be cleared explicitly, and against a server
that cannot complete registration it makes every later attempt fail the same
way. Discarding needs no extra persisted field and self-heals. This recovery
never discards an explicitly configured static client ID and never replaces the
active record until the new candidate publishes.

Decompilation of the shipped 1.4.1 assembly confirms the necessity: the
provider's `_clientId` is an in-memory field never read back from the token
cache, `TokenContainer` carries only token fields (`TokenType`, `AccessToken`,
`RefreshToken`, `ExpiresIn`, `Scope`, `ObtainedAt`), and the DCR branch runs
whenever `_clientId` is empty — so without the bridge, every cold start that
needs fresh authorization silently registers a brand-new client. The
`ResponseDelegate` fires after the SDK parses the registration response and
exposes `ClientId` and `ClientSecret`, so capture there is reliable, and an
exception thrown from it propagates and fails the connection (persist-or-fail
holds).

**Rationale:** 1.4.1's `TokenContainer` carries access token, refresh token,
type, scope, obtained-at, and expiry — but **no** registration fields — and its
cold-start path does not reliably restore a DCR-issued client ID. Without the
bridge, a restart re-registers or fails. This is narrow state adaptation, not
protocol ownership; it is scoped to be removed once the released SDK restores
registered identity itself (upstream #1658 / PR #1705). If the process exits
after DCR but before token exchange, re-registration on the next attempt is
acceptable — no second file exists solely to preserve an incomplete flow.

**Alternatives considered:**

- Keep the `mcp-oauth-metadata.json` cache as the client-ID home. Rejected: it
  is the disposable file behind defect 3 — losing it silently disables OAuth —
  and it duplicates state the token record already carries.

### D6. Explicit reauthorization withholds the access token instead of deleting credentials

**Decision:** `netclaw mcp auth <name>` runs against a candidate-local cache that
does not return the existing access token, which forces the SDK down the
authorization path. The durable token set and refresh token stay intact until
the candidate initializes and commits once. Failure or cancellation disposes
only the unpublished candidate and leaves the previous connection and
credentials untouched.

**Rationale:** An operator re-authorizing must not lose working credentials if
the new flow fails or is abandoned. Deleting durable state first to "force" the
flow would leave the server unauthenticated on any failure. Withholding the
token in a scoped view triggers the SDK's authorization branch without touching
what is on disk.

**Alternatives considered:**

- Delete the cached token before starting the flow. Rejected: a closed browser
  tab or a provider error would leave a previously working server with no
  credentials at all.

### D7. Two narrow components broker a daemon-owned browser handoff; the callback URI is derived

**Decision:** Add `McpOAuthFlowBroker` (interactive handoff only: a
cryptographically opaque one-time state tied to one server, a five-minute
lifetime bounded by `TimeProvider`, a task the SDK completes with its authorization
URL, and a task the HTTP callback completes with the code) and
`McpOAuthCredentialStore` (durable per-server token sets plus the DCR client ID,
supplying `ITokenCache` adapters). The broker performs no discovery, DCR, PKCE,
exchange, or refresh. On `POST /api/mcp/oauth/start/{name}`, the manager opens a
pending broker flow and starts an unpublished candidate whose
`AuthorizationRedirectDelegate` is owned by that flow; the SDK generates the
authorization URL, the broker returns it through the existing
`McpOAuthStartResponse`, and `GET /api/mcp/oauth/callback` validates the exact
pending state and completes the flow. The callback request is never made the
candidate's lifetime owner, so closing the tab cannot cancel a running exchange.
The start request does not own the candidate either: the manager supplies a
daemon-owned cancellation token bounded by flow expiry and shutdown. A second
start for the same server coalesces onto the active flow and receives the same
state and URL, so only one candidate and credential write can win. Polling does
not report `Completed` until token persistence, tool listing, and candidate
publication all succeed; any later initialization failure reports `Failed`.
The broker lifetime matches the existing five-minute CLI and TUI polling
timeout, so no Termina behavior changes are needed. Polling cancellation ends
client interest without cancelling the daemon-owned flow.
While an interactive authorization candidate is active, background and
invocation-triggered reconnect requests for that server coalesce onto the
interactive replacement operation rather than creating a competing candidate.
They may continue using a healthy published generation, or report
`AwaitingAuth` when none exists, but cannot publish another generation ahead of
the authorization flow.
The redirect URI is
`http://127.0.0.1:{DaemonConfig.Port}/api/mcp/oauth/callback`, never derived
from a request `Host` header. For normal startup the redirect delegate is
non-interactive and returns no code, so startup never unexpectedly opens a
browser or blocks.

**Rationale:** These two small components replace one large service that was
simultaneously protocol client, cache, state machine, and persistence layer. The
simplicity metric is fewer authorities and state machines, not fewer classes.
Deriving the port from config fixes defect 7 for any non-default local port.

**Decompiled 1.4.1 contract (verified against the shipped assembly):**

- `AuthorizationRedirectDelegate` returns the authorization code as a string;
  the SDK invokes it only after discovery, DCR, and PKCE, with the fully built
  authorization URI and the `ClientOAuthOptions.RedirectUri`. A null or empty
  return throws `McpException` — loud, non-blocking, and safe for the
  non-interactive startup variant, which maps that failure to `AwaitingAuth`.
- The SDK neither generates nor validates the OAuth `state` parameter. The
  broker injects its opaque state via
  `ClientOAuthOptions.AdditionalAuthorizationParameters["state"]` (the SDK's
  URL builder reserves only `client_id`, `redirect_uri`, `response_type`,
  `code_challenge`, `code_challenge_method`, `resource`, and `scope`), and
  Netclaw owns state validation and CSRF protection end-to-end.
- The provider contains no synchronization, and the transport's POST and
  GET/SSE paths can challenge concurrently through one provider instance. The
  broker therefore elects the first redirect-delegate invocation as the flow
  owner. It alone publishes the authorization URL, waits for the callback code,
  and returns that code to its SDK invocation. Concurrent delegates observe the
  same flow as authorization-in-progress and do not prompt, but they fail their
  request with a classified in-progress result rather than reusing the owner's
  code with a different PKCE verifier.
- The SDK imposes no timeout on the delegate; the broker bounds its own wait
  (the five-minute flow lifetime) and honors the SDK-passed cancellation token.
  Cancellation between code delivery and exchange completion consumes the
  one-time code with nothing persisted, so a cancelled or failed exchange is
  classified as requiring fresh authorization — never an exchange retry.
- OAuth triggers lazily on the first 401/403 challenge, not eagerly at
  connect, and configured scopes are a fallback: scopes advertised via
  `WWW-Authenticate` or protected-resource metadata take precedence in 1.4.1.
  When an operator configures an `Authorization` header, Netclaw does not attach
  SDK OAuth to that transport because SDK 1.4.1 would replace the header after a
  challenge; the explicit operator credential remains authoritative.

### D8. Failure modes, recovery, and diagnostics

**Decision:** Persistence and diagnostics follow a strict fail-loud ordering.

- Published-connection `StoreTokensAsync` constructs the complete replacement
  record, persists it transactionally, updates the active in-memory view, then
  returns success. Ordinary startup and reconnect candidates do the same because
  refresh may already have rotated remote state. An explicit authorization candidate keeps SDK writes local
  until initialization succeeds, then commits the complete record once before
  publication. If that commit fails, active state is unchanged and the failure
  propagates visibly.
- A token response that omits `refresh_token` retains the prior refresh token.
- Authenticated OAuth API endpoints return a structured `McpErrorResponse` for
  discovery, DCR rejection, credential persistence, and connection init. The
  anonymous browser callback preserves its existing `text/html` response and
  renders a safe actionable message for state-validation or code-exchange
  failure. The daemon logs the full exception and server context; neither JSON
  nor HTML exposes a token, code, verifier, or secret. The CLI parses
  `McpErrorResponse`, falls back to HTTP status/reason on an empty or malformed
  body, and never prints a blank error line.
- The authenticated status response carries an optional `McpErrorResponse` for
  terminal credential-storage, code-exchange, or candidate-initialization
  failures that occur after the start response has already returned.
- Connection status distinguishes `AwaitingAuth` (interaction required),
  `AuthFailed` (credentials/refresh rejected), `Unreachable` (transport), and
  `Connected` (published usable generation). An expired access token with no
  refresh token says reauthorization is required rather than degrading to a
  generic error.

Recovery paths: a failed candidate leaves the prior generation serving; a
rejected refresh surfaces `AuthFailed` and directs the operator to
`netclaw mcp auth <name>`; a disk failure fails the store call and the
connection, keeping the last active durable record authoritative; `StopAsync`
enters each server's gate, marks shutdown, rejects new reconnects, then removes
and disposes the snapshot so state publication cannot race shutdown.

**Amended during implementation:** invocation leases and the bounded drain are
deferred to the separate client-lifecycle change. A replaced or shutting-down
client is disposed once its replacement is published, so an invocation still in
flight is cancelled rather than drained. `dev` behaved the same way, so this is a
non-improvement rather than a regression, but it is a deliberate gap.

**Rationale:** Persist-before-publish is the only ordering that keeps disk and
memory from disagreeing across a restart. The status taxonomy turns defect 8's
blank error into an operator instruction.

## Risks / Trade-offs

- **[Risk]** SDK OAuth behavior differs across providers. → **Mitigation:**
  fake-server OAuth integration tests plus the existing provider reproduction
  cases; report standards/SDK defects upstream instead of re-forking the
  protocol inside Netclaw.
- **[Risk]** The SDK redirect-delegate interactive flow is unexercised in this
  codebase — today `BuildOAuthOptions` sets the delegate to return `null`,
  deliberately suppressing the SDK browser path. → **Mitigation:**
  decompilation of the shipped 1.4.1 assembly has verified the static contract
  (delegate receives the built authorization URI, returns the code, fails
  loudly on null, store failures propagate, broker state injectable — see D7);
  PR 2 still starts with a spike that proves the full flow at runtime
  (discovery, DCR, PKCE, URL delivery, callback completion, exchange,
  `ITokenCache` store) against a fake OAuth MCP server before any manual
  OAuth code is deleted.
- **[Risk]** Explicit auth is now coupled to candidate-connection creation. →
  **Mitigation:** land the D2 lifecycle boundary first, keep the candidate
  unpublished, and preserve the old connection until success.
- **[Risk]** An existing valid token could suppress an intended
  reauthorization. → **Mitigation:** explicit flows use the D6 cache view that
  withholds the cached access token without deleting durable state.
- **[Risk]** The stable SDK's concurrent-refresh race persists until upstream
  ships — decompilation confirms 1.4.1 has no synchronization anywhere in its
  auth namespace, so concurrent requests through one provider can each redeem
  the same refresh token. → **Mitigation:** track csharp-sdk#1595 / PR #1708
  (per-provider refresh single-flight); do not serialize every MCP call or
  stand up a second refresh owner as a workaround. The broker coalesces
  concurrent interactive-delegate invocations per flow (D7), and duplicate
  interactive prompts cannot occur in normal operation because the
  non-interactive delegate returns no code. The manager-level generation gate
  stays necessary regardless, because any future SDK lock is scoped to one
  `ClientOAuthProvider` instance and Netclaw still creates and replaces those.
- **[Risk]** DCR client identity is absent from stable `TokenContainer`. →
  **Mitigation:** the D5 bridge carries the effective client ID through the
  per-connection context and into `ClientOAuthOptions` until the released SDK
  (csharp-sdk#1658 / PR #1705) makes it redundant.
- **[Risk]** Replaying an ambiguous transport failure duplicates a remote side
  effect. → **Mitigation:** reconnect for later calls but never automatically
  replay the failed invocation; cancellation and tool-declared/application
  errors do not reconnect.
- **[Risk]** The secrets refactor touches non-MCP writers. → **Mitigation:**
  keep the new primitive inside `SecretsFileWriter`, preserve encryption and
  permissions, migrate read/modify/write callers mechanically, and add
  concurrent cross-section preservation tests.

## Migration Plan

Two ordered local PRs now, one dependency PR later. They may ship in one Netclaw
release; the split exists for review isolation and rollback clarity.

1. **PR 1 — single MCP client lifecycle.** `McpClientManager` becomes the sole
   lifecycle owner: generation-aware reconnect coalescing, atomic snapshot
   publication, in-flight-call draining, narrowed reconnect classification with
   no automatic invocation replay, serialized teardown. No
   OAuth protocol behavior changes. Update netclaw-dev/netclaw#1696 with the
   local lifecycle completion; do not close it.
2. **PR 2 — SDK-owned OAuth, durable boundary, diagnostics.** Replace the manual
   OAuth stack with the SDK redirect-delegate flow; add `McpOAuthFlowBroker` and
   `McpOAuthCredentialStore`; delete discovery/DCR/PKCE/exchange/refresh and the
   metadata-cache runtime dependency; add transactional `secrets.json` mutation
   and migrate callers; bind credentials to the configured resource identity;
   derive the callback port; ship structured daemon/CLI diagnostics. The
   `netclaw-operations` system skill is updated in this PR
   (MCP wiring and diagnostics change) with a `metadata.version` bump, and the
   behavioral eval suite must pass. Fixes netclaw-dev/netclaw#1475 once
   structured/bodyless error behavior is proven; mention the local-port fix in
   #297 without closing it.
3. **Official SDK upgrade (separate, later).** Once an official release contains
   csharp-sdk#1595 / PR #1708 and #1658 / PR #1705, bump the pinned package, run
   the full OAuth/reconnect regression suite, remove only the compatibility code
   the released API makes demonstrably redundant, and keep the manager-level
   lifecycle gate. Do not consume a preview or vendor a patched SDK.

Existing `mcp-oauth-metadata.json` files are ignored, not deleted — the runtime
simply stops reading them, and the durable token record (with its client ID)
becomes authoritative on the next flow.

**Rollback:** each PR is independently revertable. Reverting PR 2 restores the
manual OAuth path and the previous diagnostics; reverting PR 1 restores the old
reconnect behavior. Neither revert deletes durable credentials.

## Open Questions

None outstanding. The decision record settles the ownership boundaries (no
actor, no refresh service, no bearer middleware), the SDK-versus-Netclaw
protocol split, and the temporary client-ID bridge; the delivery plan settles PR
sequencing and the upstream-dependency deferral.
