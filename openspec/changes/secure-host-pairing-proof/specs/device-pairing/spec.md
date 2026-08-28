Cross-cutting terms use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## MODIFIED Requirements

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
