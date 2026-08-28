## Purpose

Define a versioned proof that grants one daemon-host operation to a process with access to the Netclaw key ring.

Cross-cutting terms use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Host pairing code requests require a local-control proof

The daemon SHALL expose `POST /api/local-control/v1/pairing-code` for host pairing code requests.
The request SHALL contain a proof that uses the host Netclaw Data Protection key ring.
The proof SHALL use the isolated purpose `Netclaw.LocalControl.Pairing.v1`.
A device token, bootstrap token, loopback address, or proxy address SHALL NOT replace this proof.

#### Scenario: Host CLI with the shared key ring creates a code

- **GIVEN** the CLI and daemon use the same Netclaw home
- **WHEN** the host CLI submits a valid local-control proof
- **THEN** the daemon returns a new pairing code and expiration time

#### Scenario: Remote device token cannot create a code

- **GIVEN** a remote caller has a valid device or bootstrap token
- **AND** the caller has no host key-ring proof
- **WHEN** the caller requests a pairing code
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code

### Requirement: The local-control proof has strict bounds

The proof SHALL contain protocol version `1`, operation `generate-pairing-code`, an issue time, and a 128-bit random nonce.
The daemon SHALL accept a proof for 30 seconds after its issue time.
The daemon SHALL allow no more than five seconds of future clock skew.
The daemon SHALL reject a request body larger than 4 KiB.

#### Scenario: Current proof succeeds

- **GIVEN** a valid proof was issued no more than 30 seconds ago
- **WHEN** the daemon validates the proof
- **THEN** validation succeeds

#### Scenario: Stale or future proof fails

- **GIVEN** a proof is too old or more than five seconds in the future
- **WHEN** the daemon validates the proof
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code

#### Scenario: Unsupported protocol version fails clearly

- **GIVEN** the daemon can authenticate a proof with an unsupported protocol version
- **WHEN** the daemon reads the proof
- **THEN** it returns a stable unsupported-version error
- **AND** it creates no pairing code

### Requirement: A local-control proof is single-use

The daemon SHALL accept each nonce once.
The daemon SHALL retain at most 1,024 unexpired nonces.
The daemon SHALL remove expired nonces before it checks capacity.
The daemon SHALL fail closed when the cache remains full.

#### Scenario: Repeated proof fails

- **GIVEN** the daemon accepted a proof once
- **WHEN** any caller submits the same proof again
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no second pairing code

#### Scenario: Full replay cache fails closed

- **GIVEN** the replay cache contains 1,024 unexpired nonces
- **WHEN** a caller submits another valid proof
- **THEN** the daemon returns a service-unavailable response
- **AND** the daemon creates no pairing code

### Requirement: Key-ring access defines host authority

The CLI and daemon SHALL fail clearly when the key ring is missing, unreadable, corrupt, or unsafe.
On Unix systems, Netclaw SHALL restrict the key directory to its owner before proof use.
A container operator SHALL run the CLI inside the daemon container or another process with the same persisted Netclaw home.

#### Scenario: Container CLI shares the daemon key ring

- **GIVEN** the daemon uses a persisted Netclaw home in a container
- **WHEN** the operator runs `netclaw daemon pair` inside that container
- **THEN** the CLI creates a proof that the daemon accepts

#### Scenario: Different Netclaw home fails

- **GIVEN** a CLI uses a different Netclaw home and key ring
- **WHEN** it submits its proof to the daemon
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code

