## Context

Netclaw already has the core pieces of a fail-closed exposure story in code:

- `hub-auth` reserves auto-authentication for loopback connections only
- `ExposureModeValidationService` already blocks non-local exposure when no remote
  authentication path exists
- `ExposureModeDoctorCheck` validates some exposure prerequisites offline
- the init wizard writes config and then polls daemon readiness

But the current planning artifacts do not protect one subtle but critical boundary:
loopback trust is about the final TCP hop observed by Netclaw, not about where the
original caller came from. In reverse-proxy mode, if the last hop into Netclaw is
`127.0.0.1`, `::1`, or `localhost`, then any forwarded-header mistake would make
remote traffic look identical to a trusted local operator connection. That violates
PRD-002's fail-closed posture.

The current doctor behavior also lags startup behavior. Startup already refuses
non-local exposure when there is no remote auth path, but doctor can still report a
pass in cases that startup will reject. Issue #862 also highlights an operator UX
gap: startup validation errors currently surface poorly through setup and health
check flows. The original issue scope also covered sidecar / host-managed tunnel
topologies where tunnel process detection is too coarse because the tunnel runs
outside the daemon's PID namespace or on a sibling host component.

This change tightens the planning artifacts so all validation surfaces preserve the
same trust boundary and fail-loud semantics, while preserving an explicit opt-in for
tunnel modes that cannot satisfy local process detection.

## Goals / Non-Goals

**Goals:**

- Preserve loopback auto-auth exclusively for true local operator traffic.
- Forbid reverse-proxy mode configurations where the final hop into Netclaw is
  loopback.
- Allow same-host reverse-proxy deployments only when the final hop uses a
  non-loopback internal IP.
- Allow tunnel-sidecar / host-managed tunnel deployments to bypass local process
  detection only when the operator explicitly opts in via
  `Daemon.SkipTunnelProcessCheck`.
- Ensure doctor and startup enforce the same remote-auth prerequisites for
  reverse-proxy mode.
- Ensure malformed `TrustedProxies` entries fail loudly and consistently across
  config parsing, doctor, and startup.
- Ensure setup/health-check flows surface startup validation failures as clean,
  actionable operator messages.

**Non-Goals:**

- Implementing a new authentication scheme.
- Auto-correcting proxy configs or synthesizing trusted-proxy defaults.
- Auto-detecting or silently assuming tunnel health when process detection is
  skipped.
- Relaxing the existing loopback auth contract for convenience.
- Managing nginx/Caddy/Traefik/HAProxy configuration directly.

## Decisions

### D1. Reverse-proxy trust is evaluated on the final hop, not the original client claim

When Netclaw runs behind a reverse proxy, the security boundary is the address of the
immediate peer connection into Netclaw plus the set of explicitly trusted proxies.
Forwarded headers are only meaningful after the direct peer is validated as a trusted
proxy.

Rationale:

- This preserves the existing loopback auth model from `hub-auth`.
- It avoids blessing remote traffic just because a proxy can emit
  `X-Forwarded-For` / `Forwarded` headers.
- It matches PRD-002's default-deny posture: trust must be explicit at each hop.

Alternative considered:

- Allow loopback final hop when forwarded headers are present. Rejected because
  missing, stripped, or misordered headers would collapse remote and local traffic
  into the same auto-authenticated path.

### D2. Reverse-proxy mode forbids loopback final hops

If reverse-proxy mode is configured, Netclaw rejects configurations where the final
hop from proxy to daemon uses `127.0.0.1`, `::1`, or `localhost`. This includes
same-host proxies.

Allowed same-host topology example:

- proxy listens on public/tailnet interface
- proxy forwards to daemon on a host-private non-loopback address such as
  `192.168.x.x`, `10.x.x.x`, or another explicitly configured internal address
- proxy source address or network is listed in `TrustedProxies`

Rejected topology example:

- nginx/Caddy/Traefik on the same host forwards to `http://127.0.0.1:5199`

Rationale:

- A loopback final hop is indistinguishable from a true local operator connection to
  the loopback auth scheme when forwarded-header trust fails.
- Same-host convenience is not a valid reason to blur the root-of-trust boundary.

Alternative considered:

- Allow same-host loopback forwarding with a warning. Rejected because that would
  preserve a misconfiguration that can escalate remote traffic into local trust.

### D3. `TrustedProxies` is fail-loud and all-or-nothing

Every `TrustedProxies` entry must parse as a valid IP address or CIDR. If any entry
is malformed, configuration validation, doctor, and startup all fail loudly with the
specific bad value named. There is no partial acceptance of the valid subset.

Examples that must fail:

- `"not-an-ip"`
- `"127.0.0.1/999"`
- `"10.0.0.0/not-a-mask"`
- malformed IPv6/CIDR strings

Rationale:

- Partial parsing silently changes the trust boundary.
- Operators need deterministic feedback so they know the live trust graph matches
  the declared config exactly.

Alternative considered:

- Ignore malformed entries and keep the valid ones. Rejected because it silently
  narrows or broadens trust depending on operator intent and violates the repo's
  no-silent-fallback rule.

### D4. Tunnel process detection stays fail-closed by default, with explicit operator bypass

For `tailscale-serve`, `tailscale-funnel`, and `cloudflare-tunnel`, Netclaw keeps
the current hard-fail process prerequisite by default. If the required process is
not visible locally, startup and doctor reject the configuration.

Operators may explicitly set `Daemon.SkipTunnelProcessCheck=true` to bypass only the
local process-liveness probe for those tunnel-backed modes. This supports sidecar,
container sibling, or host-managed tunnel topologies where the tunnel is real but
not discoverable from the Netclaw process.

The bypass does not relax any other validation:

- remote-auth remains required for non-local exposure
- reverse-proxy final-hop loopback rejection remains unchanged
- malformed `TrustedProxies` still fail loudly
- doctor and startup must agree on whether the process check was skipped

Rationale:

- Process detection is a coarse heuristic, not the actual trust boundary.
- Sidecar and host-managed deployments are legitimate operator-managed topologies.
- Requiring an explicit config flag preserves fail-closed defaults and avoids silent
  weakening of tunnel validation.

Alternative considered:

- Automatically skip process detection in containers or when a tunnel endpoint looks
  externally reachable. Rejected because that introduces hidden heuristics on a
  security-relevant startup gate.

### D5. Doctor and startup share the same reverse-proxy and tunnel remote-auth rules

Reverse-proxy mode requires the same remote-auth availability at doctor time that it
requires at startup time: if Netclaw is reachable beyond pure local mode, doctor must
reject configs that have no viable remote authentication path.

At minimum, doctor must align with startup on:

- required tunnel / proxy prerequisites for the selected exposure mode
- explicit `SkipTunnelProcessCheck` handling for tunnel-backed modes
- required remote-auth availability for non-local exposure
- reverse-proxy final-hop loopback rejection
- invalid `TrustedProxies` rejection

Rationale:

- `netclaw doctor` is supposed to be an operator preflight, not a weaker advisory
  layer.
- A green doctor result for a config that startup rejects is operationally misleading.

Alternative considered:

- Keep doctor advisory-only for remote-auth gaps. Rejected because startup already
  treats these as hard failures.

### D6. Setup and health-check flows surface startup validation failures as structured results

When daemon startup fails because `ExposureModeValidationService` rejects the config,
init and health-check flows must show a structured failure item containing the
validation message and remediation text. They must not degrade to:

- a raw crash/stack trace
- a generic "daemon did not become ready" timeout when process startup already
  failed synchronously

The startup failure should be captured close to `DaemonManager.Start()` and surfaced
in the wizard/health-check UI as a configuration failure.

Rationale:

- This satisfies issue #862's graceful setup requirement.
- It preserves fail-closed behavior while making the failure understandable.

Alternative considered:

- Rely on daemon logs only. Rejected because setup and doctor are operator-facing
  guidance surfaces and should not require log spelunking for expected validation
  failures.

## Risks / Trade-offs

- [Risk] Some existing same-host reverse-proxy setups may rely on loopback upstreams.
  -> Mitigation: fail loudly with explicit guidance to move the daemon bind/final hop
  to a non-loopback internal IP.
- [Risk] Doctor parity may require new probing or config-reading paths for remote-auth
  availability. -> Mitigation: reuse the same validation helpers as startup where
  possible instead of duplicating logic.
- [Risk] Operators may set `SkipTunnelProcessCheck` and assume Netclaw is now
  validating tunnel health some other way. -> Mitigation: document that the flag only
  disables local process detection and shifts tunnel-liveness responsibility fully to
  the operator.
- [Risk] Strict all-or-nothing `TrustedProxies` parsing may break configs that were
  previously tolerated. -> Mitigation: name the exact bad entry and provide valid
  examples in remediation.
- [Risk] Setup UI work may still miss some daemon crash paths unrelated to exposure
  validation. -> Mitigation: scope this change specifically to validation failures from
  `ExposureModeValidationService`, then generalize later if needed.

## Migration Plan

1. Add reverse-proxy trust-boundary requirements to `daemon-exposure` and align them
   with `hub-auth`.
2. Add or extend config/schema contracts for reverse-proxy mode,
   `SkipTunnelProcessCheck`, and `TrustedProxies`.
3. Centralize validation helpers so doctor and startup share the same decision rules,
   including the explicit tunnel process-check bypass.
4. Update init / health-check flows to surface startup validation messages directly.
5. Add regression tests for loopback final-hop rejection, explicit process-check
   bypass for tunnel-sidecar topologies, malformed trusted-proxy values,
   doctor/startup parity, and graceful setup reporting.

Rollback:

- Revert the stricter reverse-proxy validation and onboarding surfacing together.
- If rollback is required operationally, operators can return to `ExposureMode=local`
  or a non-proxy tunnel mode until the stricter rules are restored.

## Open Questions

- What is the final wire name for reverse-proxy exposure mode and its config shape in
  `DaemonConfig`?
- Should doctor be able to infer remote-auth availability from bootstrap paired-device
  files alone, or must it also validate additional scheme registrations indirectly via
  config?
