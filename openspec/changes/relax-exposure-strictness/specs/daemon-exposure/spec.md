## MODIFIED Requirements

### Requirement: Startup prerequisite validation for tunnel modes

The daemon SHALL validate that tunnel infrastructure and remote-auth prerequisites
are met before completing startup. If prerequisites are not met, the daemon SHALL
fail startup with a descriptive error. The daemon does NOT manage tunnel or proxy
processes; it validates that the declared trust boundary is safe to honor.

For `tailscale-serve`, `tailscale-funnel`, and `cloudflare-tunnel`, local tunnel
process detection SHALL remain the default prerequisite check. Operators MAY set
`Daemon.SkipTunnelProcessCheck` to `true` as an explicit opt-in to bypass only that
process-liveness check for sidecar or host-managed tunnel topologies.

When `Daemon.SkipTunnelProcessCheck` is `true`, the daemon SHALL still enforce every
other exposure requirement for the selected mode, including remote-auth
prerequisites.

#### Scenario: Tunnel mode fails startup when required process is missing by default

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is absent or `false`
- **AND** the required tunnel process is not running locally
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that the selected tunnel mode
  requires its tunnel process unless the operator explicitly opts out of the check

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

### Requirement: Doctor checks for exposure health

The `netclaw doctor` command SHALL include exposure mode health checks that validate
 tunnel / proxy infrastructure status and SHALL reject the same remote-auth and
proxy-trust configurations that daemon startup rejects.

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

### Requirement: Daemon config section in JSON schema

The `netclaw-config.v1.schema.json` SHALL validate reverse-proxy trust settings
explicitly. Any `TrustedProxies` entry SHALL be a valid IP address or CIDR string.
Malformed entries SHALL be rejected by validation instead of being ignored.

The schema SHALL also support `Daemon.SkipTunnelProcessCheck` as a boolean flag.
Its default behavior SHALL remain `false` when omitted.

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
