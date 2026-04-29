# device-pairing Specification

## Purpose

Define the bearer token authentication scheme, pairing code exchange flow,
paired device registry, device management commands, and CLI token attachment
for self-hosted remote access without an external identity provider.

## Requirements

### Requirement: Bearer token authentication scheme

The daemon SHALL register a bearer token authentication scheme that validates
device tokens on SignalR connections. The scheme SHALL read the token from the
`Authorization: Bearer <token>` header on the HTTP upgrade request. Valid
tokens SHALL produce Netclaw claims with `Operator` principal, `Verified`
transport, and the paired device ID as sender.

#### Scenario: Valid bearer token accepted

- **GIVEN** a remote connection provides a bearer token matching a paired device
- **WHEN** the bearer token scheme evaluates the connection
- **THEN** authentication succeeds with `PrincipalClassification = Operator`,
  `TransportAuthenticity = Verified`, and `SenderId` = the device name

#### Scenario: Invalid bearer token rejected

- **GIVEN** a remote connection provides a bearer token that does not match any
  paired device
- **WHEN** the bearer token scheme evaluates the connection
- **THEN** authentication fails

#### Scenario: Missing bearer token defers to other schemes

- **GIVEN** a connection provides no bearer token
- **WHEN** the bearer token scheme evaluates the connection
- **THEN** the scheme returns `NoResult` (defers to loopback or other schemes)

### Requirement: Pairing code generation

The daemon SHALL generate a short-lived pairing code when requested by a local
operator via `netclaw daemon pair`. The code SHALL be a human-readable format
(e.g., `ABCD-1234`), expire after 5 minutes, and be single-use.

#### Scenario: Generate pairing code

- **GIVEN** a local operator runs `netclaw daemon pair`
- **WHEN** the command executes
- **THEN** a pairing code is displayed with its expiration time
- **AND** the code is registered with the daemon for validation

#### Scenario: Pairing code expires

- **GIVEN** a pairing code was generated 5 minutes ago
- **WHEN** a remote client attempts to exchange the code
- **THEN** the exchange is rejected with an expiration error

#### Scenario: Pairing code is single-use

- **GIVEN** a pairing code has been successfully exchanged once
- **WHEN** another client attempts to exchange the same code
- **THEN** the exchange is rejected

### Requirement: Pairing code exchange

A remote CLI SHALL exchange a valid pairing code for a long-lived device token
via `netclaw pair <endpoint>`. The exchange SHALL occur over an unauthenticated
pairing endpoint that is separate from the main hub. The daemon SHALL prompt
the operator to name the device and store the token hash in the device
registry.

#### Scenario: Successful pairing exchange

- **GIVEN** a valid, unexpired pairing code exists
- **WHEN** a remote CLI runs `netclaw pair http://daemon:5199` and enters the
  pairing code and a device name
- **THEN** the daemon validates the code
- **AND** generates a long-lived device token
- **AND** returns the token to the remote CLI
- **AND** stores the token hash and device name in the device registry

#### Scenario: Remote CLI stores token

- **GIVEN** a successful pairing exchange returned a device token
- **WHEN** the remote CLI receives the token
- **THEN** the token is stored in `~/.netclaw/config/secrets.json` under a
  `DeviceToken` key
- **AND** the daemon endpoint is stored in config for future connections

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

The CLI's `HubConnectionBuilder` SHALL read a device token from
`~/.netclaw/config/secrets.json` and attach it as a bearer token when
connecting to a non-loopback daemon endpoint. For loopback endpoints, no token
is attached.

#### Scenario: Remote endpoint with token

- **GIVEN** `Daemon:Endpoint` is `http://remote-host:5199`
- **AND** a device token exists in `secrets.json`
- **WHEN** the CLI connects to the daemon
- **THEN** the bearer token is attached to the SignalR connection

#### Scenario: Loopback endpoint skips token

- **GIVEN** `Daemon:Endpoint` is `http://127.0.0.1:5199`
- **WHEN** the CLI connects to the daemon
- **THEN** no bearer token is attached (loopback scheme handles auth)

#### Scenario: Remote endpoint without token

- **GIVEN** `Daemon:Endpoint` is `http://remote-host:5199`
- **AND** no device token exists in `secrets.json`
- **WHEN** the CLI attempts to connect
- **THEN** the connection fails with 401
- **AND** the CLI displays a message suggesting `netclaw pair`
