## ADDED Requirements

### Requirement: Reverse proxy exposure mode

The system SHALL support `ExposureMode` value `reverse-proxy` for deployments
where the daemon is behind an operator-managed reverse proxy (nginx, Traefik,
Caddy, Kubernetes ingress, or similar). This mode SHALL NOT require any specific
tunnel process. The remote authentication guard (paired device or alternative
auth scheme) SHALL still be enforced.

#### Scenario: Reverse proxy mode starts without process check

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** the daemon starts
- **THEN** no tunnel process check is performed
- **AND** startup succeeds if at least one paired device or remote auth scheme exists

#### Scenario: Reverse proxy mode without authentication fails startup

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** no paired devices exist
- **AND** no alternative remote auth scheme is configured
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating remote authentication is required

#### Scenario: Reverse proxy mode in JSON schema

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "reverse-proxy" }`
- **WHEN** schema validation runs
- **THEN** validation passes

### Requirement: Skip tunnel process check for sidecar deployments

The system SHALL support a `Daemon.SkipTunnelProcessCheck` boolean configuration
property (default `false`). When `true` and the configured exposure mode requires
a tunnel process that is not detected locally, the daemon SHALL log a warning and
continue startup instead of aborting. The remote authentication guard SHALL still
be enforced regardless of this flag.

#### Scenario: Skip flag allows startup without local tunnel process

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **AND** the `tailscaled` process is not running locally
- **AND** at least one paired device exists
- **WHEN** the daemon starts
- **THEN** a warning is logged indicating the tunnel process was not detected
- **AND** startup succeeds

#### Scenario: Skip flag does not bypass auth guard

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **AND** no paired devices exist
- **AND** no alternative remote auth scheme is configured
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating remote authentication is required

#### Scenario: Skip flag has no effect on local mode

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **WHEN** the daemon starts
- **THEN** no tunnel validation is performed (same as without the flag)

#### Scenario: Default behavior preserved without skip flag

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is `false` (or not configured)
- **AND** the `tailscaled` process is not running locally
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating `tailscaled` is not running

### Requirement: Trusted proxy forwarded headers

The system SHALL support a `Daemon.TrustedProxies` configuration property
(string array of IP addresses or CIDR ranges, default empty). When the array is
non-empty AND the exposure mode is non-local, the daemon SHALL enable ASP.NET
`ForwardedHeaders` middleware to resolve real client IPs from `X-Forwarded-For`
and `X-Forwarded-Proto` headers. Only requests originating from listed proxy
addresses SHALL have their forwarded headers honored.

#### Scenario: Trusted proxy resolves real client IP

- **GIVEN** `Daemon.TrustedProxies` contains `"10.0.0.1"`
- **AND** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** a request arrives from connection IP `10.0.0.1` with header `X-Forwarded-For: 203.0.113.42`
- **THEN** `RemoteIpAddress` is resolved to `203.0.113.42` for authentication and rate limiting

#### Scenario: Untrusted source forwarded header ignored

- **GIVEN** `Daemon.TrustedProxies` contains `"10.0.0.1"`
- **AND** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** a request arrives from connection IP `192.168.1.50` with header `X-Forwarded-For: 203.0.113.42`
- **THEN** `RemoteIpAddress` remains `192.168.1.50` (header ignored)

#### Scenario: Empty trusted proxies disables header processing

- **GIVEN** `Daemon.TrustedProxies` is empty (or not configured)
- **AND** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** a request arrives with `X-Forwarded-For` header
- **THEN** `RemoteIpAddress` is the connection IP (header ignored)

#### Scenario: CIDR range matching

- **GIVEN** `Daemon.TrustedProxies` contains `"172.17.0.0/16"`
- **AND** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** a request arrives from connection IP `172.17.0.5` with header `X-Forwarded-For: 203.0.113.42`
- **THEN** `RemoteIpAddress` is resolved to `203.0.113.42`

#### Scenario: Forwarded headers do not activate in local mode

- **GIVEN** `Daemon.TrustedProxies` contains `"10.0.0.1"`
- **AND** `Daemon.ExposureMode` is `local`
- **WHEN** a request arrives from connection IP `10.0.0.1` with header `X-Forwarded-For: 203.0.113.42`
- **THEN** `RemoteIpAddress` remains `10.0.0.1` (middleware not active)

#### Scenario: Forward limit restricts hop count

- **GIVEN** `Daemon.TrustedProxies` contains `"10.0.0.1"`
- **AND** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** a request arrives from `10.0.0.1` with header `X-Forwarded-For: 203.0.113.42, 198.51.100.1`
- **THEN** `RemoteIpAddress` is resolved to `198.51.100.1` (rightmost untrusted hop, ForwardLimit=1)

#### Scenario: Forwarded proto header honored

- **GIVEN** `Daemon.TrustedProxies` contains `"10.0.0.1"`
- **AND** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** a request arrives from `10.0.0.1` with header `X-Forwarded-Proto: https`
- **THEN** `Request.Scheme` is set to `https`

### Requirement: Trusted proxies in JSON schema

The `netclaw-config.v1.schema.json` SHALL include `SkipTunnelProcessCheck`
(boolean, default `false`), `TrustedProxies` (array of strings, default empty),
in the `Daemon` object. The `ExposureMode` enum SHALL include `reverse-proxy`.

#### Scenario: Schema validates new Daemon properties

- **GIVEN** a config file with `"Daemon": { "SkipTunnelProcessCheck": true, "TrustedProxies": ["10.0.0.0/8"], "ExposureMode": "reverse-proxy" }`
- **WHEN** schema validation runs
- **THEN** validation passes

#### Scenario: Schema rejects invalid TrustedProxies type

- **GIVEN** a config file with `"Daemon": { "TrustedProxies": "10.0.0.1" }`
- **WHEN** schema validation runs
- **THEN** validation fails (expected array, got string)

## MODIFIED Requirements

### Requirement: Startup prerequisite validation for tunnel modes

The daemon SHALL validate that tunnel infrastructure prerequisites are met
before completing startup. If prerequisites are not met, the daemon SHALL
fail startup with a descriptive error — unless `SkipTunnelProcessCheck` is
`true`, in which case a warning SHALL be logged and startup continues.
The daemon does NOT manage tunnel processes — it only validates their presence.
For `reverse-proxy` mode, no process check is performed (no required process).

#### Scenario: Tailscale Serve mode with tailscaled running

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** the `tailscaled` process is running
- **WHEN** the daemon starts
- **THEN** startup succeeds

#### Scenario: Tailscale Serve mode without tailscaled

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** the `tailscaled` process is not running
- **AND** `Daemon.SkipTunnelProcessCheck` is `false` (or not configured)
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating `tailscaled` is not running

#### Scenario: Tailscale Funnel mode without tailscaled

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** the `tailscaled` process is not running
- **AND** `Daemon.SkipTunnelProcessCheck` is `false` (or not configured)
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating `tailscaled` is not running

#### Scenario: Cloudflare Tunnel mode with cloudflared running

- **GIVEN** `Daemon.ExposureMode` is `cloudflare-tunnel`
- **AND** the `cloudflared` process is running
- **WHEN** the daemon starts
- **THEN** startup succeeds

#### Scenario: Cloudflare Tunnel mode without cloudflared

- **GIVEN** `Daemon.ExposureMode` is `cloudflare-tunnel`
- **AND** the `cloudflared` process is not running
- **AND** `Daemon.SkipTunnelProcessCheck` is `false` (or not configured)
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating `cloudflared` is not running

#### Scenario: Local mode requires no tunnel validation

- **GIVEN** `Daemon.ExposureMode` is `local`
- **WHEN** the daemon starts
- **THEN** no tunnel prerequisite checks are performed

#### Scenario: Reverse proxy mode requires no tunnel validation

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **WHEN** the daemon starts
- **THEN** no tunnel prerequisite checks are performed

### Requirement: Doctor checks for exposure health

The `netclaw doctor` command SHALL include exposure mode health checks that
validate tunnel infrastructure status, flag unsafe configurations, and account
for the skip flag and reverse-proxy mode.

#### Scenario: Non-loopback bind without exposure mode

- **GIVEN** `Daemon.Host` is `"0.0.0.0"` or any non-loopback address
- **AND** `Daemon.ExposureMode` is `local`
- **WHEN** `netclaw doctor` runs
- **THEN** a warning is reported: non-loopback bind address without a declared
  exposure mode may make the daemon host-network reachable without the
  required authenticated-user gate

#### Scenario: Tailscale mode with healthy tunnel

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** `tailscaled` is running and serve is configured
- **WHEN** `netclaw doctor` runs
- **THEN** the exposure check passes

#### Scenario: Tailscale mode with missing tunnel and skip flag

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** `tailscaled` is not running
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **WHEN** `netclaw doctor` runs
- **THEN** a warning is reported: tunnel process not detected; operator asserts it is running externally

#### Scenario: Tailscale mode with missing tunnel without skip flag

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** `tailscaled` is not running
- **AND** `Daemon.SkipTunnelProcessCheck` is `false` (or not configured)
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported: `tailscaled` is not running

#### Scenario: Reverse proxy mode with loopback bind

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** `Daemon.Host` is `"127.0.0.1"`
- **WHEN** `netclaw doctor` runs
- **THEN** a warning is reported: mode is reverse-proxy but daemon is bound to loopback; reverse proxy may not be able to reach the daemon

#### Scenario: Reverse proxy mode without trusted proxies

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** `Daemon.TrustedProxies` is empty or not configured
- **WHEN** `netclaw doctor` runs
- **THEN** a warning is reported: no trusted proxies configured; IP-based rate limiting will use proxy IP

#### Scenario: Reverse proxy mode with valid configuration

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** `Daemon.Host` is `"0.0.0.0"`
- **AND** `Daemon.TrustedProxies` is non-empty
- **WHEN** `netclaw doctor` runs
- **THEN** the exposure check passes

### Requirement: Daemon config section in JSON schema

The `netclaw-config.v1.schema.json` SHALL include a `Daemon` object with
`Host` (string, default `"127.0.0.1"`), `Port` (integer, default `5199`),
`ExposureMode` (string enum: `local`, `tailscale-serve`, `tailscale-funnel`,
`cloudflare-tunnel`, `reverse-proxy`, default `"local"`),
`SkipTunnelProcessCheck` (boolean, default `false`),
`TrustedProxies` (array of strings, default `[]`), and
`DisableSelfUpdate` (boolean, default `false`).

#### Scenario: Schema validates valid Daemon section with new properties

- **GIVEN** a config file with `"Daemon": { "Host": "0.0.0.0", "Port": 5199, "ExposureMode": "reverse-proxy", "SkipTunnelProcessCheck": false, "TrustedProxies": ["172.17.0.0/16"] }`
- **WHEN** schema validation runs
- **THEN** validation passes

#### Scenario: Schema rejects invalid ExposureMode

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "nginx-proxy" }`
- **WHEN** schema validation runs
- **THEN** validation fails citing the invalid enum value

#### Scenario: Missing new properties use defaults

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "tailscale-funnel" }`
- **WHEN** schema validation runs
- **THEN** validation passes
- **AND** defaults resolve to `SkipTunnelProcessCheck: false`, `TrustedProxies: []`
