## MODIFIED Requirements

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
