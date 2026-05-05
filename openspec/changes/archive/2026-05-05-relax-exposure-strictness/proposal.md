## Why

Issue #862 exposed a gap between Netclaw's intended fail-closed gateway posture and
the current exposure-mode artifacts. The current specs and implementation already
preserve loopback auto-auth for true local operator traffic, and startup validation
already blocks non-local exposure without remote auth. But the planning artifacts do
not state several security-critical boundaries explicitly enough:

- reverse-proxy mode must not treat a loopback final hop into Netclaw as safe,
  because missing or broken forwarded-header trust would let remote traffic inherit
  loopback operator trust
- `netclaw doctor` must enforce the same remote-auth prerequisites as daemon startup,
  so operators do not get a false green check for configs the daemon will refuse
- issue #862 also covered tunnel-sidecar / host-managed topologies where Netclaw's
  local process detector cannot see `tailscaled` or `cloudflared`, so operators
  need an explicit opt-in escape hatch instead of being forced to disable the
  tunnel mode entirely
- malformed `TrustedProxies` entries must fail validation loudly instead of degrading
  into a broader trust decision
- setup and health-check flows must surface `ExposureModeValidationService` failures
  cleanly instead of collapsing into a generic readiness timeout or raw crash trace

This change tightens the existing exposure-mode planning to align with PRD-002's
default-deny, fail-closed security posture and with the current loopback-auth model
already defined in the hub-auth capability.

## What Changes

- Add reverse-proxy trust-boundary requirements that forbid configurations where the
  final hop into Netclaw is loopback (`127.0.0.1`, `::1`, `localhost`) in reverse-proxy
  mode, even when the proxy runs on the same host.
- Require same-host reverse proxies to use a non-loopback internal IP for the final
  hop if they want Netclaw to distinguish proxied remote traffic from true local
  operator traffic.
- Add an explicit `Daemon.SkipTunnelProcessCheck` opt-in for `tailscale-serve`,
  `tailscale-funnel`, and `cloudflare-tunnel` so sidecar / host-managed tunnel
  topologies can bypass local process detection when the operator knowingly manages
  tunnel liveness outside the daemon.
- Preserve default hard-fail behavior for tunnel modes: when
  `SkipTunnelProcessCheck` is absent or `false`, startup and doctor still reject a
  missing required tunnel process.
- Tighten doctor requirements so reverse-proxy mode checks the same remote-auth
  startup prerequisites as `ExposureModeValidationService`.
- Require invalid `TrustedProxies` values to fail schema/config validation, doctor,
  and startup loudly with actionable errors instead of silent fallback or partial
  parsing.
- Add onboarding/setup requirements so startup validation failures from
  `ExposureModeValidationService` are presented as structured setup/health-check
  failures with remediation guidance rather than raw crash output or opaque
  readiness timeouts.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `daemon-exposure`: tighten reverse-proxy trust-boundary rules, doctor/startup
  parity, opt-in tunnel process-check bypass for sidecar topologies,
  `TrustedProxies` validation, and fail-closed error handling.
- `netclaw-onboarding`: require clean surfacing of daemon startup validation
  failures during init/health-check flows.
- `hub-auth`: clarify that loopback auto-auth remains reserved for true local
  connections and must not be inherited by reverse-proxied remote traffic.

## Impact

### Affected code and systems

- `DaemonConfig` parsing/validation and JSON schema for reverse-proxy-related
  exposure settings, including `TrustedProxies` and `SkipTunnelProcessCheck`.
- `ExposureModeValidationService` and `ExposureModeDoctorCheck` so they share the
  same remote-auth, tunnel-process, and proxy-trust validation rules.
- Reverse-proxy / forwarded-header trust plumbing that determines whether a
  connection can ever qualify for loopback auto-auth.
- `netclaw init` health-check and daemon start polling so startup validation
  failures are surfaced cleanly.

### APIs and behavior

- **BREAKING (validation hardening):** reverse-proxy configurations that forward
  into Netclaw over loopback now fail validation instead of being considered a
  potentially acceptable topology.
- **BREAKING (validation hardening):** malformed `TrustedProxies` entries such as
  `"abc"`, `"127.0.0.1/999"`, or invalid mixed CIDR/IP strings now fail every
  validation surface explicitly.
- **New opt-in behavior:** tunnel modes may explicitly set
  `Daemon.SkipTunnelProcessCheck=true` to allow sidecar / host-managed tunnel
  topologies where the required tunnel process is intentionally not visible from the
  Netclaw process.
- Doctor output becomes stricter for reverse-proxy mode by rejecting configs that
  startup would reject.

### Security and operational impact

- Preserves Netclaw's loopback trust boundary so remote traffic cannot inherit the
  `LocalProcess` / `Operator` path through proxy misconfiguration.
- Removes false-positive operator feedback where doctor passes a remote-access
  configuration that startup will fail.
- Preserves fail-closed defaults for tunnel-backed modes while still supporting
  explicit sidecar deployments that would otherwise fail a coarse process probe.
- Eliminates silent degradation for malformed trusted-proxy lists.
- Improves setup UX by turning fail-closed startup validation into actionable
  operator guidance instead of stack traces or opaque daemon-not-ready results.

### Dependencies and sequencing

- Extends the existing exposure-mode and device-pairing behavior already present in
  the codebase.
- Must stay aligned with PRD-002 and the existing `hub-auth` loopback authentication
  contract.

### In scope for MVP

- Reverse-proxy final-hop restriction, loud `TrustedProxies` validation, doctor /
  startup parity, explicit `SkipTunnelProcessCheck` support for tunnel-sidecar
  topologies, and clean setup/health-check surfacing for startup validation
  failures.

### Out of scope for MVP

- Introducing a new fallback auth path for misconfigured reverse proxies.
- Inferring tunnel health automatically when operators explicitly choose to bypass
  local process detection.
- Managing or auto-repairing reverse proxy configuration outside of Netclaw.
- Broad redesign of hub authentication beyond preserving the existing loopback
  boundary.

### Source PRDs

- `PRD-002` (gateway security envelope; fail-closed and default-deny)
- `PRD-004` (CLI onboarding and operator diagnostics)
