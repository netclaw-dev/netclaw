Cross-cutting terms use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md).

## ADDED Requirements

### Requirement: Pairing security tests prove host success and remote denial

The required suite SHALL test each exposure mode against every supported credential and local-control proof class.
Each denied case SHALL prove that no pairing code was created.
The required suite SHALL not require a live tunnel provider.

#### Scenario: Every exposure mode permits a valid host proof

- **GIVEN** the test matrix contains every supported exposure mode
- **WHEN** a host caller submits a valid proof in each mode
- **THEN** each case creates one pairing code

#### Scenario: Remote credentials never replace a host proof

- **GIVEN** the caller has no valid host proof
- **WHEN** the caller uses no token, a device token, or a bootstrap token in each exposure mode
- **THEN** every case denies code generation
- **AND** every case proves that no code exists

#### Scenario: Proof and key failure matrix remains deterministic

- **GIVEN** stale, future, changed, repeated, cross-home, malformed, and unsupported proofs
- **WHEN** the required suite validates each case with virtual time
- **THEN** every result matches the reviewed security matrix
- **AND** no case needs network access or a live tunnel process

#### Scenario: Process test proves recovery after upgrade

- **GIVEN** an isolated Netclaw home contains a pre-upgrade device registry
- **WHEN** the current daemon and CLI run the host pairing procedure
- **THEN** the host receives a pairing code
- **AND** the prior device registry remains valid

