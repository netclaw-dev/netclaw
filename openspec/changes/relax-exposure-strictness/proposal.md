## Why

The daemon's startup validation crashes when `tailscaled`/`cloudflared` runs in a sidecar container or on the host (different PID namespace), blocking all Docker and Kubernetes deployments that use external tunnel infrastructure (GitHub Issue #862). Additionally, there is no exposure mode for operators who manage their own reverse proxy (nginx, Traefik, Caddy, K8s ingress) — they must either stay localhost-only or declare a specific tunnel provider they may not be using. Finally, all IP-based security (rate limiting, fail2ban lockout, loopback auth) uses raw `RemoteIpAddress` which shows the proxy's IP when behind any reverse proxy, degrading per-client protections.

## What Changes

- Add `Daemon.SkipTunnelProcessCheck` boolean config (default `false`) that downgrades the tunnel process detection from a hard startup crash to a warning log, enabling sidecar and host-level tunnel topologies
- Add `reverse-proxy` exposure mode value — declares the daemon is behind an operator-managed reverse proxy with no specific tunnel process requirement; authentication is still enforced
- Add `Daemon.TrustedProxies` config (string array of IPs/CIDRs) that enables ASP.NET `ForwardedHeaders` middleware to resolve real client IPs from `X-Forwarded-For` headers
- Update doctor checks to handle new mode and skip-flag scenarios
- Update setup wizard to offer `reverse-proxy` as an exposure mode choice

## Capabilities

### New Capabilities

(none — all changes extend the existing `daemon-exposure` capability)

### Modified Capabilities

- `daemon-exposure`: Adding `reverse-proxy` mode, `SkipTunnelProcessCheck` flag, `TrustedProxies` config with forwarded headers middleware, and updated doctor check scenarios

## Impact

- **Configuration**: `DaemonConfig` gains three new properties; JSON schema gains corresponding entries with defaults (non-breaking for existing configs)
- **Security pipeline**: `ForwardedHeaders` middleware inserted before authentication when `TrustedProxies` is non-empty; all existing `RemoteIpAddress` consumers benefit automatically
- **Startup behavior**: `ExposureModeValidationService` gains two new code paths (skip-flag warning, null-process reverse-proxy); existing hard-fail behavior unchanged for operators who don't set the flag
- **CLI**: Doctor check and wizard TUI gain new scenarios
- **Source PRDs**: PRD-002 (gateway security envelope), PRD-004 (remote access)
