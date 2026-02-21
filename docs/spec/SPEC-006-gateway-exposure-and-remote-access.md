# SPEC-006: Gateway Exposure and Remote Access Controls

Source PRDs: `PRD-002`, `PRD-004`

## Purpose

Define secure exposure modes for operator access to Netclaw management surfaces.

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

- public HTTPS access through Tailscale Funnel
- requires explicit opt-in and strong auth policy
- emits high-risk diagnostic warning

### Mode: `cloudflare-tunnel`

- public or private access through Cloudflare Tunnel
- must be paired with Cloudflare Access policy (IdP/service token)
- emits mode + policy status in diagnostics

## Security Controls

1. configuration validation rejects unsupported mode values
2. `local` mode requires no external tunnel dependency
3. public modes require authenticated access policy configuration
4. privileged actions require paired operator session approval
5. all exposure mode changes are audit logged

## CLI and UI Integration

- CLI must report effective exposure mode and policy health
- UI security page must show current mode, auth status, and warnings
- doctor command must flag public exposure without valid access policy
