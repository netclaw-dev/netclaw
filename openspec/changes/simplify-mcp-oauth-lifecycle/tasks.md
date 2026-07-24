# Tasks: Simplify MCP OAuth Ownership and Client Lifecycle

Groups 1-5 deliver PR 1 (single MCP client lifecycle). Groups 6-14 deliver PR 2
(SDK-owned OAuth, durable boundary, diagnostics). The two PRs may ship in one
release; the split exists for review isolation and rollback clarity. Tests use
synchronization signals (TaskCompletionSource, Ask-acks, `AwaitAssertAsync`) —
never `Thread.Sleep` or `Task.Delay` in orchestration.

## 1. PR 1 — Connection snapshot and generation model

- [x] 1.1 Define an immutable per-server connection snapshot type carrying the `McpClient`, its function/tool map, a monotonically increasing generation number, status metadata (including the `TimeProvider`-derived last error timestamp), and invocation lease state. Done when the published data is read-only and one lease owner can retire and drain the generation safely.
- [x] 1.2 Replace `McpClientManager`'s separate client and tool dictionaries with one published snapshot reference per server. Done when no code path reads the client and its tools as independent dictionaries.
- [x] 1.3 Update `ToolRegistry` and status reporting to read from the same snapshot so connection state, tool count, and error info always change together. Done when a single lifecycle operation is the only writer of all three.

## 2. PR 1 — Generation-aware lifecycle gate

- [x] 2.1 Add a per-server async lifecycle gate guarding create, publish, replace, and teardown. Done when every lifecycle transition for a server passes through one gate.
- [x] 2.2 Implement coalescing reconnect: capture the observed generation, enter the gate, re-read the published snapshot, and if a newer healthy generation already exists, reuse it and return. Done when concurrent reconnects that observed the same generation converge on one winner.
- [x] 2.3 Build the candidate without removing the published connection; initialize it fully (`ListToolsAsync` plus function-map construction) before publication; on init failure dispose only the candidate and keep the published connection. Done when a failed candidate leaves the prior generation serving.
- [x] 2.4 Publish the candidate atomically as the next generation, retire the replaced generation so it accepts no new invocation leases, and dispose it exactly once after its final in-flight lease ends. Done when no routine replacement interrupts an in-flight invocation and no generation is leaked or double-disposed.
- [x] 2.5 Serialize shutdown: `StopAsync` enters each server's gate, marks shutdown, rejects new leases and reconnects, allows a bounded invocation drain, cancels any remainder with the daemon shutdown token, then removes and disposes the snapshot. Done when no connection can be published after shutdown starts and no client is disposed while its invocation is still executing.

## 3. PR 1 — Invocation classification

- [x] 3.1 Format tool-declared and application MCP errors as tool results with no teardown or reconnect. Done when a tool-declared error returns a result and leaves the connection published.
- [x] 3.2 Propagate caller-token `OperationCanceledException` and unknown application exceptions with no teardown or retry. Done when cancellation reaches the caller and the shared connection stays live.
- [x] 3.3 Classify transport/session failures, return the failed invocation as an error without replay, release its snapshot lease, and only then request or await at most one coalesced reconnect for later calls. Done when an ambiguous failure never invokes the remote tool a second time and reconnect cannot wait on the caller's own lease.

## 4. PR 1 — Lifecycle and concurrency tests

- [x] 4.1 Concurrent-reconnect coalescing test: many callers observing the same generation produce exactly one candidate and one winning generation, with no client leaked or double-disposed. Drive it with `TaskCompletionSource`/Ask-acks/`AwaitAssertAsync`.
- [x] 4.2 Failed-candidate test: a candidate that fails to initialize disposes only itself; the prior generation and its tools stay available and status never advertises an empty tool set.
- [x] 4.3 Disposal-accounting test: instrument the fake client's dispose count to prove no client is leaked or disposed more than once across reconnects.
- [x] 4.4 Non-teardown test: caller cancellation and tool-declared/application errors neither reconnect nor dispose the healthy shared connection.
- [x] 4.5 Shutdown-vs-reconnect race test: with a reconnect in flight, shutdown begins; assert no connection is published after shutdown starts and every created client is disposed.
- [x] 4.6 Invocation-vs-replacement race test: hold a tool call on the prior generation while publishing a replacement; assert the call completes, the prior generation is not disposed early, and it is disposed exactly once after release.
- [x] 4.7 Self-reconnect drain test: a lease-holding invocation fails with a classified transport error and triggers reconnect; assert it releases its lease before awaiting replacement, returns without deadlock, and is not replayed.
- [x] 4.8 Shutdown-with-active-invocation test: hold a lease through shutdown, assert new leases are rejected, bounded drain occurs, cancellation releases the invocation, and disposal happens only after release.

## 5. PR 1 — Issue tracking

- [x] 5.1 Update netclaw-dev/netclaw#1696 with the local lifecycle completion. Do not close it — the upstream SDK fixes are still pending.

## 6. PR 2 — Spike: prove the SDK redirect-delegate flow

- [x] 6.1 Stand up a fake OAuth MCP server harness exposing discovery metadata, a DCR endpoint, an authorization endpoint, and a token endpoint, usable from integration tests. Done when a test can drive each endpoint.
- [x] 6.2 Prove the SDK redirect-delegate flow end-to-end against the fake server — discovery, DCR, PKCE, authorization-URL delivery via `AuthorizationRedirectDelegate`, callback completion, code exchange, and `ITokenCache` store — BEFORE any manual OAuth code is deleted. Done when a green integration test drives the full SDK path and de-risks the previously unexercised redirect delegate.
- [x] 6.3 Verify in the spike that broker-owned state injected via `AdditionalAuthorizationParameters["state"]` round-trips through the authorization URL, and that concurrent challenges from parallel transport requests coalesce onto one pending flow with a single operator prompt.

## 7. PR 2 — Transactional secrets

- [x] 7.1 Add a path-scoped, cross-process-locked read/decrypt/mutate/encrypt/replace transaction to `SecretsFileWriter`, reusing the `WebhookRouteStore` named-mutex / SHA-256 path-hash pattern. Preserve existing encryption and file-permission hardening; whole-file `Write` participates in the same path lock. Done when canonical path resolution derives the lock identity and that lock spans the latest read through atomic replacement.
- [x] 7.2 Migrate every secrets.json read-modify-write caller onto the transaction using owned field mutations applied to the latest document under the lock. Long-lived config editors and wizards replay contribution actions instead of writing snapshots captured before the lock. Done when no caller does an unlocked or stale-snapshot read-modify-write against secrets.json.
- [x] 7.3 Concurrent cross-section preservation tests: two writers updating different sections both survive, unrelated sections are preserved, a second cross-process writer observes the first's committed state before mutating, and a config editor opened before an MCP refresh cannot overwrite the refreshed token when saving another section.
- [x] 7.4 Loud-failure tests: a replacement/disk failure propagates to the caller, the prior file content stays intact, and no caller-visible state reports the update as persisted.

## 8. PR 2 — Credential store and persist-before-publish

- [x] 8.1 Add the `McpOAuthCredentialStore` singleton backing every per-connection `ITokenCache` adapter, so per-adapter in-memory dictionaries cannot diverge. Done when all adapters read one shared view.
- [x] 8.2 Implement persist-before-publish `StoreTokensAsync`: published-connection refresh writes the active record, while explicit authorization writes a flow-scoped durable pending record. Persist transactionally before advancing the matching in-memory view; on failure advance neither and propagate. Done when failed storage never advances memory and a failed candidate cannot replace active credentials.
- [x] 8.3 Retain the prior refresh token when a token response omits `refresh_token`. Done when the persisted record keeps the previous refresh token.
- [x] 8.4 Persist the DCR-issued effective client ID — and client secret, when issued — with the token record, update both from `DynamicClientRegistration.ResponseDelegate`, and seed `ClientOAuthOptions.ClientId`/`ClientSecret` from them on restart. Done when the stored client identity feeds the SDK options on reconnect.
- [x] 8.5 Restart-without-metadata-file test: a registered client identity survives restart with no `mcp-oauth-metadata.json` present, and no re-registration occurs while the stored registration is valid.
- [x] 8.6 Persist the canonical configured MCP resource identity with each credential record and compare it before returning cached tokens or seeding SDK client options. Done when repointing the same profile name withholds the old access token, refresh token, dynamically registered client ID, and client secret, reports `AwaitingAuth`, and preserves the old durable record until replacement succeeds; an explicitly configured static client ID remains authoritative.
- [x] 8.7 Resource-binding canonicalization tests cover scheme/host case, default ports, fragments, path, and query so equivalent endpoint spellings compare consistently while a changed resource fails closed.
- [x] 8.8 Legacy-binding migration test: a record without the new canonical binding supplies no token or dynamic client credentials, reports `AwaitingAuth`, preserves the old record, and never silently stamps the current profile identity onto it.
- [x] 8.9 Pending-record lifecycle tests: explicit auth stores pending credentials durably, successful candidate publication promotes them atomically, failed/expired candidates remove only pending state, and restart ignores and prunes abandoned pending records without changing active credentials.
- [x] 8.10 Invalid dynamic registration recovery test: explicit auth receiving `invalid_client` fails the current flow and records the dynamic identity as rejected; the next explicit attempt withholds that identity so SDK DCR can run with a new URL, while static configured client IDs never take this fallback.

## 9. PR 2 — Flow broker and endpoint wiring

- [x] 9.1 Add `McpOAuthFlowBroker`: a cryptographically opaque one-time state bound to a single server and flow, a five-minute `TimeProvider`-bounded lifetime matching the existing CLI/TUI timeout, a task the owner SDK delegate completes with the authorization URL, and a task the callback completes with the code. The broker performs no discovery, DCR, PKCE, exchange, or refresh. The first delegate invocation owns the URL/code exchange; concurrent delegates observe authorization in progress without another prompt and never receive the owner's code. The owner honors SDK cancellation, and the broker validates callback state itself (the SDK neither generates nor validates `state` — inject it via `AdditionalAuthorizationParameters`). At most one flow is active per server; concurrent start requests coalesce and receive the same state and URL.
- [x] 9.2 Wire the start, status, and callback endpoints keeping their existing surface: `POST /api/mcp/oauth/start/{name}` opens a pending flow and an unpublished candidate whose `AuthorizationRedirectDelegate` is owned by the flow; the status endpoint is polled unchanged; `GET /api/mcp/oauth/callback` validates the exact pending state and completes the flow.
- [x] 9.3 Give each flow and candidate a manager-owned cancellation token bounded by flow expiry and daemon shutdown, independent of start and callback HTTP request cancellation, so a returned start response or closed browser tab cannot cancel a running exchange. Make missing/mismatched/reused/expired state fail with safe callback HTML without affecting other flows. Classify a cancelled or failed code exchange as requiring fresh authorization (the one-time code is burned) — never retry the exchange.
- [x] 9.4 Make explicit reauthorization use a cache view that withholds the existing access token — forcing the SDK authorization path — while the durable token set and refresh token stay intact until the new tokens are stored.
- [x] 9.5 Derive the callback URI from `DaemonConfig.Port` (`http://127.0.0.1:{port}/api/mcp/oauth/callback`), replacing all three hard-coded 5199 sites; never derive the host from a request `Host` header. Done when a non-default port is used consistently for registration and callback.
- [x] 9.6 Tie flow status to the full candidate lifecycle: `Completed` only after durable token storage, tool listing, and publication; any subsequent initialization failure reports `Failed`. Keep the broker aligned with the existing five-minute CLI/TUI polling timeout and test that client cancellation does not cancel the daemon-owned flow.
- [x] 9.7 Concurrent-start test: two start requests for one server receive the same flow identity and URL, create one candidate, and can produce only one credential write and published generation.
- [x] 9.8 Authorization-vs-reconnect race test: while one interactive candidate is pending, background and invocation-triggered reconnects coalesce onto it, do not create or publish a competing candidate, and either keep using the healthy published generation or report `AwaitingAuth` when none exists.

## 10. PR 2 — Delete the manual OAuth stack

- [x] 10.1 Delete `McpOAuthService`'s protocol code: discovery, DCR HTTP, PKCE URL construction, code exchange, refresh redemption, and `GetValidTokenAsync`. Done when no Netclaw-owned code constructs discovery, registration, authorization, or token requests.
- [x] 10.2 Remove the `mcp-oauth-metadata.json` runtime dependency — `NetclawPaths.McpOAuthMetadataPath` and `McpOAuthServerMetadata` — and detach from `OAuthPkceService`. Legacy metadata files are ignored on every runtime path, not deleted.

## 11. PR 2 — Compatibility tests

- [x] 11.1 Prove static-header authentication and unauthenticated servers behave exactly as before: configured headers are sent unmodified, a configured `Authorization` header suppresses SDK OAuth even after a challenge, and no-auth servers connect with no OAuth activity.
- [x] 11.2 Prove OAuth support stays dormant until a challenge and that operator-provided User-Agent and non-authorization headers are never overwritten.
- [x] 11.3 Prove non-interactive startup and reconnect never open a browser or block: an OAuth-required server with no usable credentials reports `AwaitingAuth`. Poll with `AwaitAssertAsync`.

## 12. PR 2 — Diagnostics

- [x] 12.1 Return a structured `McpErrorResponse` from authenticated OAuth API failures (discovery, DCR rejection, credential persistence, connection init), include optional structured errors in authenticated terminal status responses for failures after start, and preserve safe `text/html` responses for anonymous callback validation and code-exchange failures. The daemon logs full provider/server context and no response format carries a token, code, PKCE verifier, or secret.
- [x] 12.2 Make the CLI parse `McpErrorResponse`, fall back to the HTTP status and reason phrase on an empty or malformed body, and never print a blank error line.
- [x] 12.3 Surface the connection-status taxonomy `AwaitingAuth`/`AuthFailed`/`Unreachable`/`Connected` through diagnostics; an expired access token with no refresh token reports `AwaitingAuth` and names `netclaw mcp auth <name>` as the remedy. Include a `TimeProvider`-derived last error timestamp and test that failure and recovery update state, tool count, error, and timestamp consistently without stamping recovery as a failure.
- [x] 12.4 Add the DCR-bodyless-403 regression test (netclaw-dev/netclaw#1475): a provider advertising DCR that rejects registration with HTTP 403 and no body yields a CLI message naming the failing operation and HTTP status, with full context in the daemon log.

## 13. PR 2 — Docs, skills, and evals

- [x] 13.1 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` with the new MCP OAuth wiring and diagnostics, and bump `metadata.version` in its frontmatter.
- [x] 13.2 Local behavioral eval execution was waived by the operator on 2026-07-23. A partial run against `nvidia/Qwen3.6-27B-NVFP4` stopped at the unrelated `identity_version` case (3/5, "tool call detected"); no MCP OAuth case failed before the waiver.

## 14. Final verification

- [x] 14.1 Run `dotnet build` and the full test suite; both pass. The final build used `-m:1` after a parallel MSBuild worker exited with `MSB4166`; the serial build passed with 0 warnings/errors.
- [x] 14.2 Run `dotnet slopwatch analyze`; no new violations.
- [x] 14.3 Run `./scripts/Add-FileHeaders.ps1 -Verify`; all `.cs` files have copyright headers.
- [x] 14.4 Run `git diff --check`; no whitespace or conflict-marker errors. No TUI smoke harness is needed because no Termina surface changes.
- [x] 14.5 Run `openspec validate simplify-mcp-oauth-lifecycle --type change` and `/opsx-verify simplify-mcp-oauth-lifecycle`; resolve every critical or warning finding.
- [ ] 14.6 After both implementation PRs merge, sync the delta specs with `/opsx-sync simplify-mcp-oauth-lifecycle` and archive the completed change with `/opsx-archive simplify-mcp-oauth-lifecycle`.

> Note: PR 3 (the official MCP C# SDK upgrade carrying csharp-sdk#1595/#1708 and #1658/#1705) is tracked as a separate later change; issues netclaw-dev/netclaw#1696 and #297 stay open.
