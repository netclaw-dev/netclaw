# SPEC-006: Gateway Exposure and Remote Access Controls

Source PRDs: `PRD-002`, `PRD-004`

**Implementation status:** Exposure mode configuration is implemented (Milestone 7, Phase A).
`ExposureMode` enum, `DaemonConfig`, JSON schema, startup validation
(`ExposureModeValidationService`), doctor check (`ExposureModeDoctorCheck`), and
init wizard step (`ExposureModeStepViewModel`) are all live.
`DaemonConfig` changes (bind address, exposure mode) are excluded from hot-reload
and require a manual daemon restart.

## Purpose

Define secure exposure modes for operator access to Netclaw management surfaces.

Audience selection and exposure mode are parallel controls:

- audience/profile controls who can interact with the bot in chat channels
- exposure mode controls how the daemon is reachable over the network

## Exposure Modes

### Mode: `local`

- bind loopback only
- no external network path
- default mode

### Mode: `tailscale-serve`

- tailnet-only HTTPS access
- requires Tailscale identity-based policy
- recommended remote mode

### Mode: `tailscale-funnel`

- internet-reachable HTTPS access through Tailscale Funnel
- requires explicit opt-in and strong auth policy
- emits high-risk diagnostic warning

### Mode: `cloudflare-tunnel`

- internet-reachable or private access through Cloudflare Tunnel
- must be paired with Cloudflare Access policy (IdP/service token)
- emits mode + policy status in diagnostics

## Security Controls

1. configuration validation rejects unsupported mode values
2. `local` mode requires no external tunnel dependency
3. any host-network reachable daemon access requires authenticated users
4. privileged actions require paired operator session approval
5. all exposure mode changes are audit logged

## CLI and UI Integration

- CLI must report effective exposure mode and policy health
- UI security page must show current mode, auth status, and warnings
- doctor command must flag internet-reachable exposure without valid authenticated-access policy
