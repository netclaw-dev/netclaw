## Why

The Netclaw daemon is hardcoded to bind `127.0.0.1:5199`, with no mechanism to
declare or validate the network exposure layer in front of it. This blocks
webhook ingestion (which requires a public URL), remote CLI access (which
requires tunnel-backed exposure), and Docker deployments on remote hosts (which
require configurable bind addresses). Before any of these features can ship
safely, the daemon needs a way to declare its exposure mode, validate that the
corresponding tunnel infrastructure is healthy, and refuse to start when
prerequisites are missing.

Source PRDs: `PRD-001` (Phase 2 input expansion via webhook), `PRD-002`
(SEC-005 exposure policy), `PRD-004` (Step 5 exposure mode selection in
onboarding wizard).

## What Changes

- Add `Daemon` config section with `Host`, `Port`, and `ExposureMode` properties
  to `netclaw.json` and the JSON schema.
- Replace the hardcoded `UseUrls("http://127.0.0.1:5199")` in `Program.cs` with
  config-driven bind address resolution from `Daemon.Host` and `Daemon.Port`.
- Introduce `ExposureMode` enum: `local`, `tailscale-serve`, `tailscale-funnel`,
  `cloudflare-tunnel`.
- Add startup validation that fails the daemon if a non-local exposure mode is
  declared but its tunnel prerequisites are not met (e.g., `tailscaled` not
  running, `cloudflared` not reachable).
- Add `netclaw doctor` checks for exposure mode health: tunnel process liveness,
  unsafe non-loopback bind without exposure mode, public mode without auth
  policy.
- Add an exposure mode selection step to the `netclaw init` wizard.
- The daemon does NOT manage tunnels — it validates their presence. Tunnel
  lifecycle is the operator's responsibility.

## Capabilities

### New Capabilities

- `daemon-exposure`: Exposure mode declaration, daemon bind address
  configuration, startup prerequisite validation, and doctor health checks for
  tunnel infrastructure. Covers the `Daemon` config section, `ExposureMode`
  enum, and all validation logic.

### Modified Capabilities

- `netclaw-onboarding`: Add exposure mode selection wizard step (Step 5 per
  PRD-004). The wizard currently handles security posture but not network
  exposure configuration.
- `netclaw-gateway-security`: The "Controlled exposure modes" requirement
  currently has scenarios for default-local and public-mode-requires-auth, but
  no implementation backing. This change fulfills those requirements and adds
  startup fail-closed validation.

## Impact

- **Config schema**: New `Daemon` section in `netclaw-config.v1.schema.json`
  with `Host` (string, default `"127.0.0.1"`), `Port` (integer, default
  `5199`), `ExposureMode` (string enum, default `"local"`).
- **Daemon startup**: `Program.cs` reads `Daemon:Host` and `Daemon:Port`
  instead of hardcoded URL. Startup validation gate added before
  `app.RunAsync()`.
- **Configuration types**: New `DaemonConfig` record in
  `Netclaw.Configuration`. New `ExposureMode` enum.
- **Doctor checks**: New `ExposureModeDoctorCheck` validates tunnel health and
  flags unsafe bind configurations.
- **Wizard**: New `ExposureModeStepViewModel` + `ExposureModeStepView` in the
  init wizard step sequence.
- **No breaking changes**: Existing configs without a `Daemon` section default
  to `127.0.0.1:5199` + `local` mode, preserving current behavior exactly.
- **Dependencies**: No new NuGet packages. Tunnel validation uses process
  detection (e.g., checking if `tailscaled` is running) — no SDK dependencies
  on Tailscale or Cloudflare.
