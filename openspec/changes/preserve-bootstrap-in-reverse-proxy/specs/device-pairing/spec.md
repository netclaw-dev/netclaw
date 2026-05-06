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

#### Scenario: Direct local control-plane endpoint accepts bearer token on the daemon host

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** the daemon host CLI connects directly to the daemon's configured non-loopback bind address
- **AND** the CLI provides a valid paired-device bearer token
- **WHEN** the bearer token scheme evaluates the request
- **THEN** authentication succeeds through the bearer-token path
- **AND** the request does not depend on loopback auto-auth

### Requirement: Pairing code generation stays daemon-host local

The daemon SHALL generate a short-lived pairing code only for a daemon-host local operator connection via `netclaw daemon pair`. The code SHALL be a human-readable format, expire after 5 minutes, and be single-use.

#### Scenario: Direct authenticated local control-plane request may generate a pairing code

- **GIVEN** `Daemon.ExposureMode` requires remote authentication
- **AND** the daemon host CLI authenticates with a valid paired-device bearer token
- **AND** the request reaches the daemon over a direct local control-plane connection from the daemon host
- **WHEN** `GeneratePairingCode()` runs
- **THEN** the daemon accepts the request

#### Scenario: Remote paired device cannot mint pairing codes through a reverse proxy

- **GIVEN** `Daemon.ExposureMode` is `reverse-proxy`
- **AND** a remote device authenticates with a valid paired-device bearer token
- **AND** the request reaches the daemon through a trusted reverse proxy
- **WHEN** `GeneratePairingCode()` runs
- **THEN** the daemon rejects the request because the caller is not a daemon-host local operator

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
