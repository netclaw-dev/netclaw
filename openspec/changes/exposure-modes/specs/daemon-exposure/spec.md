# daemon-exposure Specification

## Purpose

Define the daemon's network exposure configuration, bind address management,
exposure mode declaration, startup prerequisite validation, and diagnostic
health checks for tunnel infrastructure.

## Requirements

### Requirement: Exposure mode declaration

The system SHALL support an `ExposureMode` configuration property with the
following values: `local`, `tailscale-serve`, `tailscale-funnel`,
`cloudflare-tunnel`. The default value SHALL be `local`.

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

The daemon SHALL validate that tunnel infrastructure prerequisites are met
before completing startup. If prerequisites are not met, the daemon SHALL
fail startup with a descriptive error. The daemon does NOT manage tunnel
processes — it only validates their presence.

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

#### Scenario: Local mode requires no tunnel validation

- **GIVEN** `Daemon.ExposureMode` is `local`
- **WHEN** the daemon starts
- **THEN** no tunnel prerequisite checks are performed

### Requirement: Doctor checks for exposure health

The `netclaw doctor` command SHALL include exposure mode health checks that
validate tunnel infrastructure status and flag unsafe configurations.

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

#### Scenario: Cloudflare mode with missing tunnel

- **GIVEN** `Daemon.ExposureMode` is `cloudflare-tunnel`
- **AND** `cloudflared` is not running
- **WHEN** `netclaw doctor` runs
- **THEN** an error is reported: `cloudflared` is not running

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
and `ExposureMode` (string enum: `local`, `tailscale-serve`,
`tailscale-funnel`, `cloudflare-tunnel`, default `"local"`).

#### Scenario: Schema validates valid Daemon section

- **GIVEN** a config file with `"Daemon": { "Host": "0.0.0.0", "Port": 5199, "ExposureMode": "tailscale-serve" }`
- **WHEN** schema validation runs
- **THEN** validation passes

#### Scenario: Schema rejects invalid ExposureMode

- **GIVEN** a config file with `"Daemon": { "ExposureMode": "nginx-proxy" }`
- **WHEN** schema validation runs
- **THEN** validation fails citing the invalid enum value

#### Scenario: Missing Daemon section uses defaults

- **GIVEN** a config file with no `Daemon` section
- **WHEN** schema validation runs
- **THEN** validation passes
- **AND** defaults resolve to `Host: "127.0.0.1"`, `Port: 5199`,
  `ExposureMode: "local"`
