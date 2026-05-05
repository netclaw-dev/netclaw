## Context

The daemon's `ExposureModeValidationService` enforces a hard process-detection check at startup: for non-local exposure modes it calls `Process.GetProcessesByName("tailscaled")` (or `"cloudflared"`) and throws `InvalidOperationException` if not found. This assumes the tunnel process shares a PID namespace with the daemon — true on bare metal, false in Docker/K8s with sidecar tunnels.

The existing exposure mode enum (`Local`, `TailscaleServe`, `TailscaleFunnel`, `CloudflareTunnel`) does not accommodate operators who run their own reverse proxy infrastructure. There is no forwarded headers middleware anywhere in the pipeline, so IP-based security (rate limiter, `PairingExchangeGuard`, `LoopbackAuthenticationHandler`) operates on raw `Connection.RemoteIpAddress` — which is the proxy's IP when behind any intermediary.

## Goals / Non-Goals

**Goals:**

- Allow daemon to start when tunnel process runs in a sidecar/host (different PID namespace)
- Support operator-managed reverse proxy deployments without mandating a specific tunnel provider
- Enable correct client IP resolution via standard `X-Forwarded-For` header processing
- Preserve all existing security invariants (auth requirement, loopback trust boundary, fail-closed defaults)

**Non-Goals:**

- Implementing Tailscale identity header (`Tailscale-User-Login`) as an auth scheme (future work)
- Cloudflare Access JWT validation as an auth scheme (future work)
- Auto-detecting proxy topology or container networking
- TLS termination at the daemon level (proxies handle TLS)

## Decisions

### Decision 1: `SkipTunnelProcessCheck` flag rather than soft-warning by default

**Choice:** Add an opt-in boolean rather than changing the default behavior.

**Rationale:** Bare-metal operators rely on the hard failure to detect when their tunnel crashes. Changing it to a warning globally would silently degrade safety for the majority of existing deployments. The flag is narrow: it only suppresses process detection, not the auth guard.

**Alternatives considered:**
- Always warn (never throw): Loses safety net for bare-metal users
- Connectivity probe instead of process check: Requires the tunnel endpoint to be known at startup, adds network dependency to startup path, and doesn't work for all proxy types

### Decision 2: `reverse-proxy` as a new enum value with no required process

**Choice:** Add `ExposureMode.ReverseProxy` that returns `null` from `GetRequiredProcessName()`, skipping the process check entirely.

**Rationale:** This mode declares intent ("I have my own proxy") without coupling to a specific technology. The auth guard still runs — the security posture is identical to `tailscale-funnel` minus the process check. The name `reverse-proxy` is industry-standard and self-documenting.

**Alternatives considered:**
- `network` mode: Too vague — implies raw network exposure without protection
- Just use `local` + bind `0.0.0.0`: Skips the auth guard (local mode doesn't require paired devices), creating a security hole

### Decision 3: Explicit `TrustedProxies` config with ASP.NET `ForwardedHeadersMiddleware`

**Choice:** Add `Daemon.TrustedProxies` (string array of IPs/CIDRs) that configures `ForwardedHeadersOptions.KnownProxies`/`KnownNetworks`. Middleware only activates when the list is non-empty AND mode is non-local.

**Rationale:** This is the standard ASP.NET Core pattern. The middleware rewrites `RemoteIpAddress` before any authentication handler runs, so all existing IP consumers benefit without code changes. The explicit list prevents header-spoofing attacks — untrusted sources cannot influence IP resolution.

**Alternatives considered:**
- Trust all `X-Forwarded-For` headers: Trivially spoofable, violates default-deny posture
- Per-mode automatic trust (e.g., always trust Docker bridge): Too magical, breaks in non-standard networks

### Decision 4: Middleware placement before authentication

**Choice:** Insert `UseForwardedHeaders()` immediately after routing, before `UseAuthentication()`.

**Rationale:** `LoopbackAuthenticationHandler` must see the resolved client IP, not the proxy IP. The rate limiter (configured as endpoint middleware) also needs the real IP. ASP.NET Core's middleware pipeline processes in registration order — forwarded headers must resolve before any security layer inspects `RemoteIpAddress`.

**Failure modes:**
- If middleware is placed AFTER auth: Loopback handler would see proxy IP, potentially granting operator claims to the proxy itself (if proxy is on localhost). This would be a privilege escalation.
- If `TrustedProxies` is misconfigured (wrong IPs): Middleware ignores the header, `RemoteIpAddress` stays as connection IP. Degraded rate limiting but no security bypass since auth still requires bearer token.

### Decision 5: `ForwardLimit = 1` default

**Choice:** Only trust one hop of `X-Forwarded-For` by default.

**Rationale:** Most deployments have exactly one proxy in front of the daemon. Multi-hop scenarios (CDN → load balancer → daemon) are uncommon in self-hosted agent deployments. Operators with multiple hops can increase this via future `Daemon.ForwardLimit` config.

## Risks / Trade-offs

- **[Risk] Operator forgets TrustedProxies when using reverse-proxy mode** → Doctor check warns "No trusted proxies configured; IP-based rate limiting will use proxy IP." Degraded but not insecure (auth still required).

- **[Risk] Operator sets SkipTunnelProcessCheck but tunnel is genuinely down** → Warning log at startup; daemon starts but tunnel traffic won't reach it. No data loss or security issue — requests simply fail to arrive.

- **[Risk] ForwardedHeaders middleware adds latency** → Negligible; it's a single header parse per request with O(1) lookup against KnownProxies set. No measurable impact.

- **[Trade-off] No auto-detection of Docker/sidecar topology** → Operators must explicitly opt in. This is intentional: auto-detection would be fragile and violates "no silent fallbacks."
