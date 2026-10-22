## MODIFIED Requirements

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
