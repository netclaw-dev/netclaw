# daemon-exposure Specification

## Purpose

Define the daemon's network exposure configuration, bind address management,
exposure mode declaration, startup prerequisite validation, and diagnostic
health checks for tunnel and reverse-proxy infrastructure.

## Requirements

### Requirement: Exposure mode declaration

The system SHALL support an `ExposureMode` configuration property with the
following values: `local`, `tailscale-serve`, `tailscale-funnel`,
`cloudflare-tunnel`, `reverse-proxy`. The default value SHALL be `local`.

#### Scenario: Default exposure mode

- **GIVEN** no `Daemon.ExposureMode` is configured
- **WHEN** the daemon starts
- **THEN** the effective exposure mode is `local`

#### Scenario: Explicit exposure mode

- **GIVEN** `Daemon.ExposureMode` is set to `tailscale-serve`
- **WHEN** the daemon starts
- **THEN** the effective exposure mode is `tailscale-serve`

#### Scenario: Invalid exposure mode rejected

- **GIVEN** `Daemon.ExposureMode` is set to an unrecognized value
- **WHEN** configuration validation runs
- **THEN** validation fails with a descriptive error naming the invalid value
  and listing valid options

### Requirement: Configurable daemon bind address

The system SHALL support `Daemon.Host` (string, default `"127.0.0.1"`) and
`Daemon.Port` (integer, default `5199`) configuration properties. The daemon
SHALL bind to the address constructed from these properties at startup.

#### Scenario: Default bind address

- **GIVEN** no `Daemon.Host` or `Daemon.Port` is configured
- **WHEN** the daemon starts
- **THEN** the daemon binds to `http://127.0.0.1:5199`

#### Scenario: Custom bind address

- **GIVEN** `Daemon.Host` is `"0.0.0.0"` and `Daemon.Port` is `5200`
- **WHEN** the daemon starts
- **THEN** the daemon binds to `http://0.0.0.0:5200`

#### Scenario: Custom port only

- **GIVEN** `Daemon.Port` is `5200` and `Daemon.Host` is not configured
- **WHEN** the daemon starts
- **THEN** the daemon binds to `http://127.0.0.1:5200`

### Requirement: Startup prerequisite validation for tunnel modes

The daemon SHALL validate that tunnel infrastructure and remote-auth
prerequisites are met before completing startup. If prerequisites are not met,
the daemon SHALL fail startup with a descriptive error. The daemon does NOT
manage tunnel or proxy processes; it validates that the declared trust
boundary is safe to honor.

For `tailscale-serve`, `tailscale-funnel`, and `cloudflare-tunnel`, local
tunnel process detection SHALL remain the default prerequisite check.
Operators MAY set `Daemon.SkipTunnelProcessCheck` to `true` as an explicit
opt-in to bypass only that process-liveness check for sidecar or host-managed
tunnel topologies.

When `Daemon.SkipTunnelProcessCheck` is `true`, the daemon SHALL still enforce
every other exposure requirement for the selected mode, including remote-auth
prerequisites.

#### Scenario: Tailscale Serve mode with tailscaled running

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** the `tailscaled` process is running
- **WHEN** the daemon starts
- **THEN** startup succeeds

#### Scenario: Tailscale Serve mode without tailscaled

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** the `tailscaled` process is not running
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating `tailscaled` is not running

#### Scenario: Tunnel mode fails startup when required process is missing by default

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is absent or `false`
- **AND** the required tunnel process is not running locally
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that the selected tunnel mode
  requires its tunnel process unless the operator explicitly opts out of the check

#### Scenario: Tailscale Funnel mode without tailscaled

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** the `tailscaled` process is not running
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
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating `cloudflared` is not running

#### Scenario: Tunnel sidecar topology may skip process detection when explicitly configured

- **GIVEN** `Daemon.ExposureMode` is `cloudflare-tunnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **AND** the required tunnel process is not visible locally because the tunnel runs
  in a sidecar or host-managed topology
- **AND** at least one remote authentication path exists
- **WHEN** the daemon starts
- **THEN** startup does not fail solely because the local process probe did not find
  the tunnel process

#### Scenario: Reverse-proxy mode requires remote authentication

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** no paired devices exist
- **AND** no alternative remote authentication scheme is configured
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that reverse-proxy mode requires
  at least one remote authentication path before remote traffic is accepted

#### Scenario: Reverse-proxy mode rejects loopback final hop

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the final hop from the reverse proxy into Netclaw uses `127.0.0.1`, `::1`,
  or `localhost`
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that loopback auto-auth is
  reserved for true local operator traffic and cannot be inherited through a
  reverse proxy

#### Scenario: Same-host reverse proxy allowed with non-loopback final hop

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the reverse proxy runs on the same machine as Netclaw
- **AND** the final hop into Netclaw uses a non-loopback internal IP
- **AND** the proxy source is covered by `TrustedProxies`
- **AND** at least one remote authentication path exists
- **WHEN** the daemon starts
- **THEN** startup succeeds

#### Scenario: Malformed TrustedProxies entry fails startup loudly

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** `Daemon.TrustedProxies` contains an invalid entry such as `"not-an-ip"`
  or `"127.0.0.1/999"`
- **WHEN** the daemon starts
- **THEN** startup fails with a descriptive error naming the invalid entry
- **AND** the daemon does not silently ignore or partially accept the remaining
  entries

#### Scenario: Local mode requires no tunnel validation

- **GIVEN** `Daemon.ExposureMode` is `local`
- **WHEN** the daemon starts
- **THEN** no tunnel prerequisite checks are performed

### Requirement: Doctor checks for exposure health

The `netclaw doctor` command SHALL include exposure mode health checks that
validate tunnel / proxy infrastructure status and SHALL reject the same
remote-auth and proxy-trust configurations that daemon startup rejects.

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

#### Scenario: Tailscale mode with missing tunnel

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** `tailscaled` is not running
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported: `tailscaled` is not running

#### Scenario: Doctor errors when tunnel process is missing and process checks are not skipped

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** `Daemon.SkipTunnelProcessCheck` is absent or `false`
- **AND** `tailscaled` is not running locally
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported explaining that the required tunnel process is not
  running

#### Scenario: Doctor honors explicit tunnel process-check bypass

- **GIVEN** `Daemon.ExposureMode` is `tailscale-serve`
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **AND** `tailscaled` is not running locally because the tunnel is managed outside
  the Netclaw process namespace
- **AND** at least one remote-auth path exists
- **WHEN** `netclaw doctor` runs
- **THEN** doctor does not report the missing local tunnel process as an error

#### Scenario: Cloudflare mode with missing tunnel

- **GIVEN** `Daemon.ExposureMode` is `cloudflare-tunnel`
- **AND** `cloudflared` is not running
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported: `cloudflared` is not running

#### Scenario: Reverse-proxy mode without remote auth is an error

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** no paired devices exist or other remote-auth path is available
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported explaining that reverse-proxy mode would start
  fail because no remote client can authenticate

#### Scenario: Reverse-proxy mode with loopback final hop is an error

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the final hop from proxy to daemon is configured as `127.0.0.1`, `::1`, or
  `localhost`
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported explaining that loopback final-hop proxying would
  let remote traffic inherit local operator trust if forwarded-header trust fails

#### Scenario: Malformed TrustedProxies entry fails doctor loudly

- **GIVEN** `Daemon.TrustedProxies` contains an invalid IP or CIDR string
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported naming the invalid entry
- **AND** doctor does not continue as if the malformed entry were absent

### Requirement: Exposure mode does not reload without restart

Changing the exposure mode or daemon bind address SHALL NOT take effect through
hot-reload. These changes SHALL require a full daemon restart.

#### Scenario: Exposure mode change ignored during hot-reload

- **GIVEN** the daemon is running with `Daemon.ExposureMode` set to `local`
- **WHEN** the operator changes `Daemon.ExposureMode` to `tailscale-serve` in
  the config file
- **AND** the config hot-reload triggers
- **THEN** the daemon continues operating with `local` mode
- **AND** the daemon logs a warning that exposure mode changes require restart

### Requirement: Daemon config section in JSON schema

The `netclaw-config.v1.schema.json` SHALL include a `Daemon` object with
`Host` (string, default `"127.0.0.1"`), `Port` (integer, default `5199`),
`ExposureMode` (string enum: `local`, `tailscale-serve`,
`tailscale-funnel`, `cloudflare-tunnel`, `reverse-proxy`, default `"local"`),
and explicit reverse-proxy trust settings.

Any `TrustedProxies` entry SHALL be a valid IP address or CIDR string.
Malformed entries SHALL be rejected by validation instead of being ignored.

The schema SHALL also support `Daemon.SkipTunnelProcessCheck` as a boolean flag.
Its default behavior SHALL remain `false` when omitted.

#### Scenario: Schema validates valid Daemon section

- **GIVEN** a config file with `"Daemon": { "Host": "0.0.0.0", "Port": 5199, "ExposureMode": "tailscale-serve" }`
- **WHEN** schema validation runs
- **THEN** validation passes

#### Scenario: Schema rejects invalid ExposureMode

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "nginx-proxy" }`
- **WHEN** schema validation runs
- **THEN** validation fails citing the invalid enum value

#### Scenario: Schema rejects malformed TrustedProxies entry

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "reverse-proxy",
  "TrustedProxies": ["not-an-ip"] }`
- **WHEN** schema validation runs
- **THEN** validation fails citing the malformed `TrustedProxies` entry

#### Scenario: Schema rejects invalid CIDR in TrustedProxies

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "reverse-proxy",
  "TrustedProxies": ["127.0.0.1/999"] }`
- **WHEN** schema validation runs
- **THEN** validation fails citing the invalid CIDR value

#### Scenario: Schema accepts explicit tunnel process-check bypass flag

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "tailscale-serve",
  "SkipTunnelProcessCheck": true }`
- **WHEN** schema validation runs
- **THEN** validation passes for the flag shape

#### Scenario: Missing Daemon section uses defaults

- **GIVEN** a config file with no `Daemon` section
- **WHEN** schema validation runs
- **THEN** validation passes
- **AND** defaults resolve to `Host: "127.0.0.1"`, `Port: 5199`,
  `ExposureMode: "local"`
