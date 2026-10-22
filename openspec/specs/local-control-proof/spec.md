# local-control-proof Specification

## Purpose

Define a versioned proof that grants one daemon-host operation to a process with access to the Netclaw key ring.

This capability uses these [engineering glossary](../../../docs/spec/GLOSSARY.md) terms:

- [Local-control proof](../../../docs/spec/GLOSSARY.md#local-control-proof)
- [Pairing code](../../../docs/spec/GLOSSARY.md#pairing-code)
- [Authority](../../../docs/spec/GLOSSARY.md#authority)
- [Durable and ephemeral](../../../docs/spec/GLOSSARY.md#durable-and-ephemeral)

## Authority and State Flow

```text
Host authority path
  durable host key ring
    -> host CLI creates one ephemeral operation proof
    -> configured daemon endpoint authenticates the proof
    -> process-local replay cache records the nonce
    -> daemon pairing subsystem creates one ephemeral pairing code

Remote pairing path
  remote CLI
    -> advertised proxy or tunnel endpoint exchanges the pairing code
    -> device registry stores the token hash
    -> remote CLI receives the device token
```

The diagram is schematic.
It omits HTTP error mapping, rate limits, and transport encryption.

| Decision or data | Owner | Lifetime |
|---|---|---|
| Proof creation | Host CLI | Call-local |
| Direct local-control endpoint | Host CLI from daemon configuration | Call-local |
| Advertised remote pairing endpoint | Host CLI from operator client state | Durable input |
| Proof validation | Local-control endpoint | Call-local |
| Accepted nonces | Proof validator | Process-local |
| Pending pairing code | Daemon pairing subsystem | Process-local |
| Pairing transaction order | Daemon pairing subsystem | Process-local |
| Host key ring | Data Protection provider | Durable |

| Request fact | Host authority | Expected result |
|---|---|---|
| Valid proof in any exposure mode | Granted | Create one pairing code |
| Loopback address without a proof | Denied | Return unauthorized |
| Forwarded loopback address without a proof | Denied | Return unauthorized |
| Device bearer token without a proof | Denied | Return unauthorized |
| Valid repeated proof | Denied | Return unauthorized |

## Requirements

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

### Requirement: Each local-control operation has its own purpose chain

Each operation SHALL use its own Data Protection purpose chain:

| Operation | Operation byte | Purpose chain |
|---|---|---|
| `generate-pairing-code` | `1` | `Netclaw.LocalControl.Pairing.v1` |
| `read-provider-oauth-result` | `2` | `Netclaw.LocalControl.ProviderOAuthResult.v1`, then the OAuth state |

A proof for one operation SHALL fail authentication for every other operation.
A provider OAuth result proof for one state SHALL fail authentication for every other state.
The CLI SHALL NOT create a provider OAuth result proof without a state.

#### Scenario: A pairing proof cannot read provider tokens

- **GIVEN** the host CLI created a valid unused pairing proof
- **WHEN** any caller sends that proof to the provider OAuth result endpoint
- **THEN** the daemon returns an unauthorized response
- **AND** the response contains no token

#### Scenario: A provider OAuth result proof cannot create a pairing code

- **GIVEN** the host CLI created a valid unused provider OAuth result proof
- **WHEN** any caller sends that proof to the pairing-code endpoint
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code

#### Scenario: A proof for another state fails

- **GIVEN** the daemon holds completed results for states `state-a` and `state-b`
- **AND** a caller has a valid unused proof for `state-b`
- **WHEN** the caller asks for the result of `state-a` with that proof
- **THEN** the daemon returns an unauthorized response
- **AND** the response contains no token

### Requirement: Provider OAuth tokens require a local-control proof

The daemon SHALL expose `POST /api/local-control/v1/provider-oauth-result` for host provider OAuth result reads.
The request body SHALL contain the OAuth state and a provider OAuth result proof for that state.
The daemon SHALL validate the proof before it reads the OAuth flow.
The daemon SHALL return the access token, refresh token, account ID, and expiration only for a valid proof.
The daemon SHALL return `409` when the flow has no completed result.
A device token, bootstrap token, loopback address, or proxy address SHALL NOT replace this proof.
The proof SHALL use the lifetime, clock-skew, body-size, rate-limit, and single-use rules of the pairing proof.

`GET /api/provider/oauth/status/{state}` SHALL NOT return a token to any caller.
Its response SHALL contain only the flow status, a has-token flag, and the expiration.

| Request fact | Host authority | Expected result |
|---|---|---|
| Valid state-bound proof in any exposure mode | Granted | Return the tokens |
| Device or bootstrap token without a proof | Denied | Return unauthorized |
| Loopback or forwarded loopback address without a proof | Denied | Return unauthorized |
| Pairing proof | Denied | Return unauthorized |
| Proof for another state | Denied | Return unauthorized |
| Repeated or expired proof | Denied | Return unauthorized |
| Status request from any caller | Not applicable | Return flags without tokens |

#### Scenario: Host CLI reads the tokens in a tunnel mode

- **GIVEN** the daemon uses `cloudflare-tunnel` mode
- **AND** the operator completed the provider browser callback for state `state-a`
- **WHEN** the host CLI sends a valid unused proof for `state-a`
- **THEN** the daemon returns the access token and the refresh token

#### Scenario: Remote device token cannot read the tokens

- **GIVEN** the daemon uses `tailscale-funnel` mode
- **AND** a remote paired device sends its device token through the tunnel
- **AND** the request has no valid proof
- **WHEN** the device asks for the provider OAuth result
- **THEN** the daemon returns an unauthorized response
- **AND** the response contains no token

#### Scenario: Status never carries tokens

- **GIVEN** the provider OAuth flow for state `state-a` is complete
- **WHEN** a loopback caller in `local` mode or a paired device in any mode reads its status
- **THEN** the response reports `Completed` and a has-token flag
- **AND** the response contains no access token, refresh token, or account ID

#### Scenario: A repeated proof fails

- **GIVEN** the daemon accepted a provider OAuth result proof for `state-a`
- **WHEN** any caller sends the same proof again
- **THEN** the daemon returns an unauthorized response
- **AND** the response contains no token

#### Scenario: A stale proof fails

- **GIVEN** a provider OAuth result proof was issued 31 seconds ago
- **WHEN** the host CLI sends it
- **THEN** the daemon returns an unauthorized response

#### Scenario: A pending flow has no result

- **GIVEN** the provider OAuth flow for state `state-a` waits for its browser callback
- **WHEN** the host CLI sends a valid proof for `state-a`
- **THEN** the daemon returns `409`

### Requirement: The host CLI fails loudly when the result read fails

The host CLI SHALL read the provider OAuth result only after the status reports `Completed`.
The CLI SHALL stop the OAuth flow with a visible error when the daemon refuses the read.
The CLI SHALL print upgrade guidance when the daemon returns `404` or `unsupported_protocol_version`.
The CLI SHALL NOT fall back to token fields in the status response.

#### Scenario: An older daemon gives upgrade guidance

- **GIVEN** the daemon has no provider OAuth result endpoint
- **WHEN** the host CLI reads the result of a completed flow
- **THEN** the CLI shows an error that tells the operator to use the same daemon and CLI version
- **AND** the CLI saves no provider credential

### Requirement: The host CLI protects proof transport

The host CLI SHALL derive the local-control endpoint from the daemon configuration in the same Netclaw home.
The host CLI SHALL NOT use paired-client endpoint state for a local-control operation.
The host CLI MAY display the paired-client endpoint as the remote exchange instruction.
The CLI SHALL keep the request endpoint and advertised endpoint as separate values.
The host CLI SHALL NOT send the proof through an HTTP proxy or an automatic redirect.
The host CLI SHALL NOT attach a device or bootstrap bearer token to the proof request.
These transport rules SHALL apply to the pairing-code request and to the provider OAuth result read.
The direct endpoint MAY use a configured loopback or non-loopback daemon bind address.
The selected exposure mode SHALL NOT prevent a valid host proof from creating a pairing code.
The selected exposure mode SHALL NOT prevent a valid host proof from reading a completed provider OAuth result.

The proof authenticates key-ring possession instead of network location.
The proof does not provide channel confidentiality for a plain HTTP non-loopback path.

#### Scenario: Remote client endpoint does not receive the proof

- **GIVEN** `client/config.json` contains `https://remote.example`
- **AND** the local daemon binds to `0.0.0.0:5199`
- **WHEN** the operator runs `netclaw daemon pair`
- **THEN** the CLI posts the proof directly to `http://127.0.0.1:5199`
- **AND** the CLI sends no request to `https://remote.example`
- **AND** the CLI can display `netclaw pair https://remote.example` for the remote device

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

#### Scenario: Reverse-proxy mode keeps host recovery available

- **GIVEN** the daemon uses `reverse-proxy` mode
- **AND** the host has no device token
- **AND** the host CLI has the shared key ring
- **WHEN** the host CLI sends a valid proof to the configured daemon endpoint
- **THEN** the daemon creates a pairing code
- **AND** the operator does not change the exposure mode

#### Scenario: Explicit non-loopback bind remains available

- **GIVEN** the daemon binds to `192.168.1.20:5199` in a remote exposure mode
- **AND** the host CLI has the shared key ring
- **WHEN** the CLI sends a valid proof to `http://192.168.1.20:5199`
- **THEN** the daemon creates a pairing code
- **AND** the daemon does not require a device token for this operation

#### Scenario: Forwarded local appearance does not grant authority

- **GIVEN** a remote caller reaches the local-control endpoint through a proxy
- **AND** the request contains a loopback source address or forwarded loopback header
- **AND** the request has no valid local-control proof
- **WHEN** the caller requests a pairing code
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no pairing code

#### Scenario: A copied proof shows the transport limit

- **GIVEN** an on-path observer copies a valid unused proof from a plain HTTP request
- **WHEN** the observer submits that proof before the host request succeeds
- **THEN** the daemon can accept only the first request
- **AND** the single-use nonce cannot identify which caller owns the copied proof

#### Scenario: Provider OAuth result read uses the direct endpoint

- **GIVEN** the daemon uses `tailscale-serve` mode and listens on `127.0.0.1:5199`
- **AND** `client/config.json` contains `https://host.tailnet.example`
- **AND** the host CLI holds the bootstrap device token
- **WHEN** the provider OAuth flow reaches `Completed`
- **THEN** the CLI posts the state and its proof to `http://127.0.0.1:5199/api/local-control/v1/provider-oauth-result`
- **AND** the request has no bearer token
- **AND** the CLI sends no proof to `https://host.tailnet.example`

### Requirement: The local-control proof has strict bounds

The proof SHALL contain protocol version `1`, one operation, an issue time, and a 128-bit random nonce.
The operation SHALL be `generate-pairing-code` or `read-provider-oauth-result`.
The shared codec SHALL use this exact 26-byte plaintext layout before Data Protection:

| Offset | Size | Field |
|---|---|---|
| 0 | 1 byte | Protocol version |
| 1 | 1 byte | Operation; `1` means code generation, `2` means provider OAuth result read |
| 2 | 8 bytes | Signed Unix milliseconds, big-endian |
| 10 | 16 bytes | Nonce |

The codec owns byte serialization and the purpose chain. The daemon validator owns supported-version and operation decisions.
The validator SHALL refuse a proof whose operation byte differs from the requested operation.
A layout change SHALL require a new protocol version.
The daemon SHALL accept a proof for 30 seconds after its issue time.
The daemon SHALL allow no more than five seconds of future clock skew.
The daemon SHALL reject a request body larger than 4 KiB.

The daemon SHALL count only refused local-control requests against a limit for each observed source address.
A refused request is a request with an invalid body or a proof that the validator does not accept.
The daemon SHALL allow at most 10 refused requests for each source address in one fixed one-second window.
The window SHALL use the daemon `TimeProvider`.
This limit SHALL cover both local-control endpoints.
The daemon SHALL validate the proof before it applies this limit.
A request with a valid proof SHALL NOT count against the limit, and the limit SHALL NOT refuse it.
A refused request above the limit SHALL return `429` instead of its normal refusal.
`LocalControlRequests` owns this count. The count is process-local and is not durable.

#### Scenario: Current proof succeeds

- **GIVEN** a valid proof was issued 12 seconds ago
- **WHEN** the daemon validates the proof
- **THEN** validation succeeds

#### Scenario: Version-one codec preserves the fixed byte vector

- **GIVEN** version `1`, operation `1`, timestamp `0x010203040506`, and nonce `00112233445566778899AABBCCDDEEFF`
- **WHEN** the shared codec serializes these values
- **THEN** its plaintext hex is `0101000001020304050600112233445566778899AABBCCDDEEFF`
- **AND** the reader independently accepts those bytes with the same field values

#### Scenario: Authenticated malformed plaintext fails

- **GIVEN** authenticated plaintext contains 25 or 27 bytes instead of 26
- **WHEN** the codec reads that plaintext
- **THEN** it rejects the payload rather than inferring a different layout

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

#### Scenario: A valid proof succeeds during a flood of refused requests

- **GIVEN** a tunnel agent forwarded 30 requests without a valid proof from loopback in the current second
- **WHEN** the host CLI sends a valid provider OAuth result proof from loopback in that second
- **THEN** the daemon returns `200` with the tokens
- **AND** a valid pairing proof in that second also creates a pairing code

#### Scenario: Refused requests above the limit return 429

- **GIVEN** one source address sent 10 requests with invalid proofs in the current second
- **WHEN** the same address sends an eleventh request with an invalid proof in that second
- **THEN** the daemon returns `429`
- **AND** the daemon creates no pairing code and returns no tokens

#### Scenario: The refusal count resets after the window

- **GIVEN** one source address sent 11 requests with invalid proofs in one second
- **WHEN** the window ends and the source sends another request with an invalid proof
- **THEN** the daemon returns `401`, not `429`

### Requirement: A local-control proof is single-use

The daemon SHALL accept each nonce once.
The daemon SHALL retain an accepted nonce through the inclusive proof-lifetime boundary.
The daemon SHALL retain at most 1,024 unexpired nonces.
The daemon SHALL remove expired nonces before it checks capacity.
The daemon SHALL fail closed when the cache remains full.

#### Scenario: Repeated proof fails

- **GIVEN** the daemon accepted nonce `00112233445566778899AABBCCDDEEFF`
- **WHEN** any caller submits the same proof again
- **THEN** the daemon returns an unauthorized response
- **AND** the daemon creates no second pairing code

#### Scenario: Repeated proof fails at the lifetime boundary

- **GIVEN** the daemon accepts a proof exactly 30 seconds after its issue time
- **WHEN** any caller submits the same proof again at that time
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
