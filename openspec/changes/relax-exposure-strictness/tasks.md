## 1. Planning alignment and capability deltas

- [x] 1.1 Update the `daemon-exposure` spec delta to define reverse-proxy final-hop
  trust rules, doctor/startup parity, opt-in tunnel process-check bypass semantics,
  and fail-loud `TrustedProxies` validation.
- [x] 1.2 Update the `netclaw-onboarding` spec delta to require clean surfacing of
  startup validation failures during init / health-check flows.
- [x] 1.3 Cross-check the wording against `hub-auth` so loopback auto-auth remains
  reserved for true local operator traffic.

## 2. Reverse-proxy trust-boundary hardening

- [x] 2.1 Add reverse-proxy mode validation that rejects configurations where the
  final hop into Netclaw is `127.0.0.1`, `::1`, or `localhost`.
- [x] 2.2 Allow same-host reverse-proxy topologies only when the final hop uses a
  non-loopback internal IP and the proxy is explicitly trusted.
- [x] 2.3 Update operator-facing diagnostics/remediation text to explain why loopback
  final-hop proxying is rejected and how to move to a non-loopback internal address.

## 3. `TrustedProxies` validation and config/schema updates

- [x] 3.1 Update `DaemonConfig` / related config contracts to represent reverse-proxy
  trust settings, including `TrustedProxies`.
- [x] 3.2 Update `netclaw-config.v1.schema.json` so `TrustedProxies` entries are
  validated as explicit IP/CIDR strings with no silent extra properties.
- [x] 3.3 Ensure malformed `TrustedProxies` entries fail validation loudly in all
  surfaces, including examples such as `not-an-ip` and invalid CIDR masks.

## 4. Startup and doctor parity

- [x] 4.1 Extend `ExposureModeValidationService` so reverse-proxy mode enforces the
  same remote-auth prerequisites as other non-local exposure paths.
- [x] 4.1a Extend tunnel-mode validation so local process detection remains the
  default hard-fail gate, but `Daemon.SkipTunnelProcessCheck=true` explicitly skips
  that check for sidecar / host-managed tunnel topologies.
- [x] 4.2 Extend `ExposureModeDoctorCheck` so it rejects the same reverse-proxy and
  remote-auth misconfigurations that startup rejects.
- [x] 4.2a Extend `ExposureModeDoctorCheck` so it honors the same explicit
  `SkipTunnelProcessCheck` bypass that startup uses for tunnel-backed modes.
- [x] 4.3 Reuse shared validation logic or equivalent centralized rules so doctor and
  startup cannot drift on proxy trust, remote-auth requirements, or malformed
  `TrustedProxies` handling.

## 5. Graceful setup and health-check reporting (#862)

- [x] 5.1 Capture `ExposureModeValidationService` startup failures at daemon start so
  init/health-check flows can distinguish config rejection from generic readiness
  timeout.
- [x] 5.2 Surface those failures in the wizard/health-check UI as structured failure
  items with remediation guidance rather than raw stack traces or generic "did not
  become ready" messages.
- [x] 5.3 Ensure non-wizard startup/health-check surfaces report the same validation
  failures cleanly for operators.

## 6. Tests

- [x] 6.1 Add validation tests for reverse-proxy mode rejecting loopback final hops.
- [x] 6.2 Add validation tests for same-host reverse-proxy mode succeeding only with a
  non-loopback internal final hop.
- [x] 6.3 Add config/schema/doctor/startup tests for malformed `TrustedProxies`
  entries failing loudly.
- [x] 6.3a Add config/schema tests for `Daemon.SkipTunnelProcessCheck` with valid
  boolean values and default-false behavior when omitted.
- [x] 6.4 Add in-process ASP.NET reverse-proxy integration tests using `TestServer`
  patterns, following existing tests like
  `src/Netclaw.Daemon.Tests/Security/SessionHubAuthorizationTests.cs` and
  `src/Netclaw.Daemon.Tests/Security/PairingExchangeEndpointTests.cs`.
- [x] 6.5 Add a reverse-proxy integration test proving a trusted non-loopback proxy
  plus `X-Forwarded-For` rewrites the effective client IP before auth evaluation,
  so unauthenticated remote traffic does NOT inherit loopback auth.
- [x] 6.6 Add a reverse-proxy integration test proving a trusted non-loopback proxy
  plus a valid bearer token still succeeds through the normal remote-auth path
  after forwarded-header rewriting.
- [x] 6.7 Add a reverse-proxy integration test proving forwarded headers from an
  untrusted proxy source are ignored and the direct peer IP remains authoritative.
- [x] 6.8 Add reverse-proxy integration tests proving forwarded headers affect
  IP-based protections such as pairing exchange and rate limiting using the
  rewritten client IP for a supported trusted-proxy topology.
- [x] 6.9 Add parity tests proving doctor rejects the same remote-auth/proxy-trust
  configurations that startup rejects.
- [x] 6.9a Add parity tests proving tunnel modes fail by default when the required
  process is missing, but succeed past that gate when
  `SkipTunnelProcessCheck=true` and the remaining prerequisites are satisfied.
- [x] 6.10 Add onboarding / health-check tests proving startup validation failures are
  surfaced as actionable setup failures.

## 7. Verification

- [x] 7.1 Run `openspec validate "relax-exposure-strictness"`.
- [x] 7.2 Verify the change text still aligns with `PRD-002` fail-closed / default-deny
  principles and the existing `hub-auth` loopback behavior.
