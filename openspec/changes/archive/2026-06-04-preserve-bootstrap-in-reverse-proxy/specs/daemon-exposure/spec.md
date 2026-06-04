## MODIFIED Requirements

### Requirement: Startup prerequisite validation for tunnel modes

The daemon SHALL validate that tunnel infrastructure and remote-auth prerequisites are met before completing startup. If prerequisites are not met, the daemon SHALL fail startup with a descriptive error. The daemon does NOT manage tunnel or proxy processes; it validates that the declared trust boundary is safe to honor.

For `tailscale-serve`, `tailscale-funnel`, and `cloudflare-tunnel`, local tunnel process detection SHALL remain the default prerequisite check. Operators MAY set `Daemon.SkipTunnelProcessCheck` to `true` as an explicit opt-in to bypass only that process-liveness check for sidecar or host-managed tunnel topologies.

When `Daemon.SkipTunnelProcessCheck` is `true`, the daemon SHALL still enforce every other exposure requirement for the selected mode, including remote-auth prerequisites.

Before remote-auth validation fails a setup-owned first launch, the daemon SHALL allow daemon-owned bootstrap seeding to create the initial local paired device/token required for the local control-plane path.

#### Scenario: Tunnel mode fails startup when required process is missing by default

- **GIVEN** `Daemon.ExposureMode` is `tailscale-funnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is absent or `false`
- **AND** the required tunnel process is not running locally
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that the selected tunnel mode requires its tunnel process unless the operator explicitly opts out of the check

#### Scenario: Tunnel sidecar topology may skip process detection when explicitly configured

- **GIVEN** `Daemon.ExposureMode` is `cloudflare-tunnel`
- **AND** `Daemon.SkipTunnelProcessCheck` is `true`
- **AND** the required tunnel process is not visible locally because the tunnel runs in a sidecar or host-managed topology
- **AND** at least one remote authentication path exists
- **WHEN** the daemon starts
- **THEN** startup does not fail solely because the local process probe did not find the tunnel process

#### Scenario: Reverse-proxy mode requires remote authentication after bootstrap seeding is considered

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** no paired devices exist before startup validation begins
- **AND** no alternative remote authentication scheme is configured
- **AND** bootstrap seeding is not allowed or does not produce a paired device
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that reverse-proxy mode requires at least one remote authentication path before remote traffic is accepted

#### Scenario: Reverse-proxy mode rejects loopback final hop

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the final hop from the reverse proxy into Netclaw uses `127.0.0.1`, `::1`, or `localhost`
- **WHEN** the daemon starts
- **THEN** startup fails with an error explaining that loopback auto-auth is reserved for true local operator traffic and cannot be inherited through a reverse proxy

#### Scenario: Same-host reverse proxy allowed with non-loopback final hop

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the reverse proxy runs on the same machine as Netclaw
- **AND** the final hop into Netclaw uses a non-loopback internal IP
- **AND** the proxy source is covered by `TrustedProxies`
- **AND** at least one remote authentication path exists after bootstrap seeding is considered
- **WHEN** the daemon starts
- **THEN** startup succeeds
