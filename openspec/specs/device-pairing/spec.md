# device-pairing Specification

## Purpose

Define the bearer token authentication scheme, pairing code exchange flow,
paired device registry, device management commands, and CLI token attachment
for self-hosted remote access without an external identity provider.
## Requirements
### Requirement: Bearer token authentication scheme

The daemon SHALL register a bearer token authentication scheme that validates device tokens on SignalR and HTTP control-plane connections. The scheme SHALL read the token from the `Authorization: Bearer <token>` header on the request. Valid tokens SHALL produce Netclaw claims with `Operator` principal, `Verified` transport, and the paired device ID as sender.

In exposure modes that require remote authentication, this scheme SHALL remain eligible even when the control-plane endpoint is loopback. Loopback origin alone SHALL NOT suppress bearer-token authentication in those modes.

#### Scenario: Valid bearer token accepted

- **GIVEN** a remote connection provides a bearer token matching a paired device
- **WHEN** the bearer token scheme evaluates the connection
- **THEN** authentication succeeds with `PrincipalClassification = Operator`, `TransportAuthenticity = Verified`, and `SenderId` = the device name

#### Scenario: Invalid bearer token rejected

- **GIVEN** a remote connection provides a bearer token that does not match any paired device
- **WHEN** the bearer token scheme evaluates the connection
- **THEN** authentication fails

#### Scenario: Missing bearer token defers to other schemes

- **GIVEN** a connection provides no bearer token
- **WHEN** the bearer token scheme evaluates the connection
- **THEN** the scheme returns `NoResult` (defers to loopback or other schemes)

#### Scenario: Loopback control-plane endpoint still accepts bearer token in reverse-proxy mode

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** a daemon-host CLI connects to a loopback control-plane endpoint
- **AND** the CLI provides a valid paired-device bearer token
- **WHEN** the bearer token scheme evaluates the request
- **THEN** authentication succeeds through the bearer-token path
- **AND** the request does not depend on loopback auto-auth

#### Scenario: Direct local control-plane endpoint accepts bearer token on the daemon host

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the daemon host CLI connects directly to the daemon's configured non-loopback bind address
- **AND** the CLI provides a valid paired-device bearer token
- **WHEN** the bearer token scheme evaluates the request
- **THEN** authentication succeeds through the bearer-token path
- **AND** the request does not depend on loopback auto-auth

### Requirement: Pairing code exchange

A remote CLI SHALL exchange a valid pairing code for a long-lived device token via `netclaw pair <endpoint>`.
The exchange SHALL use an unauthenticated endpoint that is separate from the main hub.
The daemon SHALL validate the code before it checks the device name.
The daemon SHALL consume the code only after it stores the new device.

#### Scenario: Successful pairing exchange

- **GIVEN** a valid and unexpired pairing code exists
- **WHEN** a remote CLI submits the code and a unique device name
- **THEN** the daemon stores the device and token hash
- **AND** the daemon returns the raw device token once
- **AND** the daemon consumes the pairing code

#### Scenario: Remote CLI stores token

- **GIVEN** a successful pairing exchange returned a device token
- **WHEN** the remote CLI receives the token
- **THEN** it stores the token in `secrets.json` under `DeviceToken`
- **AND** it stores the daemon endpoint for later connections

#### Scenario: Duplicate device name preserves the code

- **GIVEN** a valid and unexpired pairing code exists
- **AND** the requested device name already exists
- **WHEN** the remote CLI submits the code and name
- **THEN** the daemon returns a conflict response
- **AND** the pairing code remains valid until its normal expiration

#### Scenario: Registry failure preserves the code

- **GIVEN** a valid and unexpired pairing code exists
- **WHEN** the device registry write fails
- **THEN** the request fails visibly
- **AND** the pairing code remains valid until its normal expiration

#### Scenario: Invalid code cannot probe device names

- **GIVEN** a caller has no valid pairing code
- **WHEN** the caller submits a known or guessed device name
- **THEN** the daemon rejects the code before a device-name lookup

#### Scenario: Concurrent exchange permits one success

- **GIVEN** two requests submit the same valid code concurrently
- **WHEN** the daemon processes both requests
- **THEN** exactly one request can register a device
- **AND** the other request cannot reuse the consumed code

### Requirement: Paired device registry

The daemon SHALL maintain a registry of paired devices at
`~/.netclaw/config/devices.json`. The registry SHALL store device name, token
hash (NOT the raw token), creation timestamp, and last-used timestamp. The
registry SHALL be readable by the operator via `netclaw daemon devices`.

#### Scenario: List paired devices

- **GIVEN** two devices are paired: `aaron-laptop` and `aaron-desktop`
- **WHEN** the operator runs `netclaw daemon devices`
- **THEN** the output lists both devices with their names, creation dates, and
  last-used timestamps

#### Scenario: Revoke a paired device

- **GIVEN** a device `aaron-laptop` is paired
- **WHEN** the operator runs `netclaw daemon devices revoke aaron-laptop`
- **THEN** the device is removed from the registry
- **AND** the device's token is no longer accepted for authentication

#### Scenario: Last-used timestamp updated on connection

- **GIVEN** a paired device connects with a valid bearer token
- **WHEN** the connection is authenticated
- **THEN** the device's last-used timestamp is updated in the registry

### Requirement: Non-local exposure requires paired device or auth scheme

When the daemon's exposure mode is non-local, startup validation SHALL verify
that at least one paired device exists OR an alternative authentication scheme
(e.g., OIDC) is configured. If neither condition is met, startup SHALL fail.

#### Scenario: Non-local mode with paired devices starts successfully

- **GIVEN** exposure mode is `tailscale-serve`
- **AND** one or more paired devices exist
- **WHEN** the daemon starts
- **THEN** startup succeeds

#### Scenario: Non-local mode with no auth fails startup

- **GIVEN** exposure mode is `tailscale-serve`
- **AND** no paired devices exist
- **AND** no alternative auth scheme is configured
- **WHEN** the daemon starts
- **THEN** startup fails with error indicating no authentication is configured
  for remote access

### Requirement: CLI attaches bearer token for remote connections

The CLI's control-plane clients SHALL read a device token from `~/.netclaw/config/secrets.json` and attach it as a bearer token when connecting to any endpoint that requires remote authentication. Pure local-mode loopback endpoints MAY skip token attachment.

#### Scenario: Remote endpoint with token

- **GIVEN** `Daemon:Endpoint` is `http://remote-host:5199`
- **AND** a device token exists in `secrets.json`
- **WHEN** the CLI connects to the daemon
- **THEN** the bearer token is attached to the SignalR connection

#### Scenario: Local-mode loopback endpoint skips token

- **GIVEN** `Daemon.ExposureMode` is `local`
- **AND** `Daemon:Endpoint` is `http://127.0.0.1:5199`
- **WHEN** the CLI connects to the daemon
- **THEN** no bearer token is attached

#### Scenario: Reverse-proxy loopback endpoint attaches token

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** `Daemon:Endpoint` is `http://127.0.0.1:5199`
- **AND** a device token exists in `secrets.json`
- **WHEN** the CLI connects to the daemon
- **THEN** the bearer token is attached
- **AND** the CLI does not assume loopback auth will authorize the connection

#### Scenario: Remote-auth-required endpoint without token fails

- **GIVEN** the resolved daemon endpoint requires remote authentication
- **AND** no device token exists in `secrets.json`
- **WHEN** the CLI attempts to connect
- **THEN** the connection fails with 401
- **AND** the CLI displays a message suggesting `netclaw pair`

### Requirement: Pairing code generation stays daemon-host local

The daemon SHALL generate a five-minute single-use pairing code only through `netclaw daemon pair` and the local-control endpoint.
The SignalR hub SHALL NOT expose pairing code generation.
The daemon SHALL NOT use request source addresses or device bearer tokens as host-origin proof.

#### Scenario: Direct authenticated local control-plane request may generate a pairing code

- **GIVEN** any configured exposure mode is active
- **AND** the host CLI submits a valid local-control proof
- **WHEN** `netclaw daemon pair` runs
- **THEN** the daemon creates and returns a pairing code

#### Scenario: Remote paired device cannot mint pairing codes through a reverse proxy

- **GIVEN** tunnel or proxy traffic reaches the daemon through loopback
- **AND** the remote caller has no local-control proof
- **WHEN** the caller requests a pairing code
- **THEN** the daemon rejects the request
- **AND** the daemon creates no pairing code

### Requirement: Pairing upgrade preserves durable device state

The upgrade SHALL preserve device records, valid device tokens, and exposure settings.
Operators SHALL update the daemon and host CLI together, then restart the daemon.
The CLI SHALL NOT fall back to the removed hub method.

#### Scenario: Current daemon and CLI pair successfully

- **GIVEN** the operator updated and restarted both components
- **WHEN** the host runs `netclaw daemon pair`
- **THEN** the new local-control flow succeeds
- **AND** previously paired remote devices remain valid

#### Scenario: Mixed versions fail without fallback

- **GIVEN** only the daemon or CLI supports the local-control protocol
- **WHEN** the host runs `netclaw daemon pair`
- **THEN** the command fails with guidance to update both components
- **AND** the command does not call the legacy hub method

#### Scenario: Host re-authentication uses normal pairing

- **GIVEN** the host has key-ring access but has no valid device token
- **WHEN** another host command requires a device token
- **THEN** the operator can generate a code through local control
- **AND** the operator can pair the host through the normal exchange endpoint
