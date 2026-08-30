This capability uses these [engineering glossary](../../../../../docs/spec/GLOSSARY.md) terms:

- [Local-control proof](../../../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Pairing code](../../../../../docs/spec/GLOSSARY.md#pairing-code)
- [Device token](../../../../../docs/spec/GLOSSARY.md#device-token)
- [Authority](../../../../../docs/spec/GLOSSARY.md#authority)
- [Durable and ephemeral](../../../../../docs/spec/GLOSSARY.md#durable-and-ephemeral)

## Pairing State Flow

```text
host proof -> create process-local code
remote code + device name -> validate code -> write durable device -> consume code
                                               |
                                               +-> failure keeps the code
```

The diagram is schematic.
It omits rate limits, token hashing, and HTTP status mapping.

| Decision or data | Owner | Lifetime |
|---|---|---|
| Pending code | Pairing code service | Process-local |
| Exchange order | Pairing coordinator | Process-local |
| Raw device token | Pairing coordinator | Call-local |
| Device record and token hash | Device registry | Durable |

## Credential Lifecycle

| Credential | Authority | Lifetime | Recovery |
|---|---|---|---|
| Local-control proof | One named host operation | 30 seconds and one use | Run `netclaw daemon pair` again |
| Pairing code | One successful device registration | Five minutes and one use | Create a new code on the daemon host |
| Device token | Paired-device access | Until operator revocation | Use the normal pairing flow again |

The pairing code and the device token are bearer credentials.
The device token has no automatic expiration or refresh flow.
Pairing-code expiration does not invalidate an existing device token.

## MODIFIED Requirements

### Requirement: Pairing code exchange

A remote CLI SHALL exchange a valid pairing code for a long-lived device token via `netclaw pair <endpoint>`.
The exchange SHALL use an unauthenticated endpoint that is separate from the main hub.
The daemon SHALL validate the code before it checks the device name.
The daemon SHALL consume the code only after it stores the new device.
The remote CLI SHALL not persist a token or endpoint after a failed exchange.

#### Scenario: Successful pairing exchange

- **GIVEN** the valid code is `ABCD-EFGH`
- **WHEN** a remote CLI submits the code with device name `tablet`
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
- **AND** the requested device name `laptop` already exists
- **WHEN** the remote CLI submits the code and name
- **THEN** the daemon returns a conflict response
- **AND** the pairing code remains valid until its normal expiration

#### Scenario: Duplicate retry uses the same code

- **GIVEN** code `ABCD-EFGH` received a conflict for device name `laptop`
- **WHEN** the remote CLI retries `ABCD-EFGH` with device name `tablet`
- **THEN** the daemon stores `tablet`
- **AND** the daemon consumes `ABCD-EFGH`

#### Scenario: Duplicate conflict gives a safe retry

- **GIVEN** the daemon rejects device name `laptop` as a duplicate
- **WHEN** the remote CLI reports the conflict
- **THEN** it tells the operator to select a different device name
- **AND** it tells the operator to reuse the same unexpired code

#### Scenario: Expired code requires a new code

- **GIVEN** code `ABCD-EFGH` has expired
- **WHEN** the remote CLI submits it
- **THEN** the daemon rejects the exchange without a registry change
- **AND** the CLI tells the operator to run `netclaw daemon pair` again

#### Scenario: Registry failure preserves the code

- **GIVEN** a valid and unexpired pairing code exists
- **WHEN** the device registry write fails
- **THEN** the request fails visibly
- **AND** the pairing code remains valid until its normal expiration

#### Scenario: Invalid code cannot probe device names

- **GIVEN** a caller submits invalid code `ZZZZ-ZZZZ`
- **WHEN** the caller submits known name `laptop` or unknown name `guess`
- **THEN** the daemon rejects the code before a device-name lookup
- **AND** both requests use the same unauthorized response

#### Scenario: Concurrent exchange permits one success

- **GIVEN** two requests submit the same valid code concurrently
- **WHEN** the daemon processes both requests
- **THEN** exactly one request can register a device
- **AND** the other request cannot reuse the consumed code

### Requirement: Pairing code generation stays daemon-host local

The daemon SHALL generate a five-minute single-use pairing code only through `netclaw daemon pair` and the local-control endpoint.
The SignalR hub SHALL NOT expose pairing code generation.
The daemon SHALL NOT use request source addresses or device bearer tokens as host-origin proof.

#### Scenario: Valid host proof may generate a pairing code

- **GIVEN** any configured exposure mode is active
- **AND** the host CLI submits a valid local-control proof
- **WHEN** `netclaw daemon pair` runs
- **THEN** the daemon creates and returns a pairing code

#### Scenario: Device bearer token does not add host authority

- **GIVEN** a remote device has a valid token for `laptop`
- **AND** it has no local-control proof
- **WHEN** it requests a new pairing code
- **THEN** the daemon returns an unauthorized response
- **AND** it creates no pairing code

#### Scenario: Forwarded loopback traffic does not grant host authority

- **GIVEN** remote tunnel or proxy traffic reaches the daemon through loopback
- **AND** the remote caller has no local-control proof
- **WHEN** the caller requests a pairing code
- **THEN** the daemon rejects the request
- **AND** the daemon creates no pairing code

## ADDED Requirements

### Requirement: Device token lifetime and recovery

The device token SHALL remain valid until the operator revokes its device record.
The daemon SHALL NOT require token refresh or automatic token renewal.
A pairing code expiration SHALL NOT change an existing device token.
A client that loses a token SHALL use the normal pairing flow again.

#### Scenario: Pairing code expires after a device pairs

- **GIVEN** device `tablet` has a valid device token
- **AND** the pairing code that created it has expired
- **WHEN** `tablet` authenticates with its device token
- **THEN** the daemon accepts the token
- **AND** the client does not refresh the token

#### Scenario: Lost token requires normal pairing and old-record revocation

- **GIVEN** a client lost its token and its old device record still exists
- **WHEN** the client needs access again
- **THEN** the operator creates a new pairing code on the daemon host
- **AND** the client pairs with a unique replacement name
- **AND** the operator revokes the old device record after replacement access works

#### Scenario: Revoked token can use normal pairing

- **GIVEN** an operator revoked a device token
- **WHEN** the client needs access again
- **THEN** the operator creates a new pairing code on the daemon host
- **AND** the client uses `netclaw pair <endpoint>`

## MODIFIED Requirements

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
