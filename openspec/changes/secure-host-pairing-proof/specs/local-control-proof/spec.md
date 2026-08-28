## Purpose

Define a versioned proof that grants one daemon-host operation to a process with access to the Netclaw key ring.

This capability uses these [engineering glossary](../../../../../docs/spec/GLOSSARY.md) terms:

- [Local-control proof](../../../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Authority](../../../../../docs/spec/GLOSSARY.md#authority)
- [Durable and ephemeral](../../../../../docs/spec/GLOSSARY.md#durable-and-ephemeral)

## Authority and State Flow

```text
durable host key ring
  -> host CLI creates one ephemeral operation proof
  -> direct host endpoint authenticates the proof
  -> process-local replay cache records the nonce
  -> pairing coordinator creates one ephemeral pairing code
```

The diagram is schematic.
It omits HTTP error mapping and the later remote pairing exchange.

| Decision or data | Owner | Lifetime |
|---|---|---|
| Proof creation | Host CLI | Call-local |
| Proof validation | Local-control endpoint | Call-local |
| Accepted nonces | Proof validator | Process-local |
| Pending pairing code | Pairing code service | Process-local |
| Pairing transaction order | Pairing coordinator | Process-local |
| Host key ring | Data Protection provider | Durable |

## ADDED Requirements

### Requirement: Host pairing code requests require a local-control proof

The daemon SHALL expose `POST /api/local-control/v1/pairing-code` for host pairing code requests.
The request SHALL contain a proof that uses the host Netclaw Data Protection key ring.
The proof SHALL use the isolated purpose `Netclaw.LocalControl.Pairing.v1`.
A device token, bootstrap token, loopback address, or proxy address SHALL NOT replace this proof.

#### Scenario: Host CLI with the shared key ring creates a code

- **GIVEN** the CLI and daemon use the same Netclaw home
- **AND** the daemon listens on `0.0.0.0:5199`
- **WHEN** the host CLI sends a valid proof to `http://127.0.0.1:5199`
- **THEN** the daemon returns a new pairing code and expiration time

#### Scenario: Remote device token cannot create a code

- **GIVEN** a remote caller has a valid device or bootstrap token
- **AND** the caller has no host key-ring proof
- **WHEN** the caller requests a pairing code
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code

### Requirement: The host CLI protects proof transport

The host CLI SHALL derive the local-control endpoint from the daemon configuration in the same Netclaw home.
The host CLI SHALL NOT use paired-client endpoint state for this operation.
The host CLI SHALL NOT send the proof through an HTTP proxy or an automatic redirect.
The host CLI SHALL NOT attach a device or bootstrap bearer token to the proof request.

#### Scenario: Remote client endpoint does not receive the proof

- **GIVEN** `client/config.json` contains `https://remote.example`
- **AND** the local daemon binds to `0.0.0.0:5199`
- **WHEN** the operator runs `netclaw daemon pair`
- **THEN** the CLI posts the proof directly to `http://127.0.0.1:5199`
- **AND** the CLI sends no request to `https://remote.example`

#### Scenario: Redirect cannot export the proof

- **GIVEN** the local-control endpoint returns an HTTP redirect to another origin
- **WHEN** the host CLI requests a pairing code
- **THEN** the CLI rejects the redirect
- **AND** the CLI does not send the proof to the redirect target

#### Scenario: HTTP proxy cannot observe the proof

- **GIVEN** the host environment configures an HTTP proxy
- **WHEN** the host CLI requests a pairing code
- **THEN** the CLI connects directly to the daemon endpoint
- **AND** the proxy receives no proof or device token

### Requirement: The local-control proof has strict bounds

The proof SHALL contain protocol version `1`, operation `generate-pairing-code`, an issue time, and a 128-bit random nonce.
The daemon SHALL accept a proof for 30 seconds after its issue time.
The daemon SHALL allow no more than five seconds of future clock skew.
The daemon SHALL reject a request body larger than 4 KiB.

#### Scenario: Current proof succeeds

- **GIVEN** a valid proof was issued 12 seconds ago
- **WHEN** the daemon validates the proof
- **THEN** validation succeeds

#### Scenario: Stale or future proof fails

- **GIVEN** a proof was issued 31 seconds ago or six seconds in the future
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

- **GIVEN** the daemon accepted nonce `00112233445566778899AABBCCDDEEFF`
- **WHEN** any caller submits the same proof again
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no second pairing code

#### Scenario: Full replay cache fails closed

- **GIVEN** the replay cache contains 1,024 unexpired nonces
- **WHEN** a caller submits another valid proof
- **THEN** the daemon returns a service-unavailable response
- **AND** the daemon creates no pairing code

### Requirement: Key-ring access defines host authority

The Data Protection provider SHALL create the key directory on first use.
The CLI and daemon SHALL fail clearly when the key path is unreadable, corrupt, or not a directory.
On Unix systems, Netclaw SHALL restrict a new or existing key directory to its owner before proof use.
A container operator SHALL run the CLI inside the daemon container or another process with the same persisted Netclaw home.

#### Scenario: First use creates an owner-only key directory

- **GIVEN** the Netclaw home has no `keys` directory
- **WHEN** the CLI or daemon creates the Data Protection provider
- **THEN** Netclaw creates the directory
- **AND** Unix grants access only to the owner

#### Scenario: A file at the key path fails clearly

- **GIVEN** the `keys` path contains a regular file
- **WHEN** the CLI or daemon creates the Data Protection provider
- **THEN** creation fails visibly
- **AND** Netclaw does not continue with a new fallback key ring

#### Scenario: Container CLI shares the daemon key ring

- **GIVEN** the daemon uses a persisted Netclaw home in a container
- **WHEN** the operator runs `netclaw daemon pair` inside that container
- **THEN** the CLI creates a proof that the daemon accepts

#### Scenario: Different Netclaw home fails

- **GIVEN** the daemon uses `/srv/netclaw-a`
- **AND** the CLI uses `/srv/netclaw-b`
- **WHEN** it submits its proof to the daemon
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code
