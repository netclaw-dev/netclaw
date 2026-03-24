# manifest-signature-verification Specification

## Purpose

Define minisign Ed25519 signature verification behavior for the binary feed
manifest, ensuring cryptographic authenticity before trusting manifest contents.

## Requirements

### Requirement: Minisign signature format parsing

The system SHALL parse minisign detached signature files (`.sig`) into their
component parts: untrusted comment line, signature algorithm identifier, key ID,
and Ed25519 signature bytes. The parser SHALL reject signatures that do not
conform to the minisign format.

#### Scenario: Parse valid minisign signature file

- **WHEN** a valid minisign `.sig` file is provided
- **THEN** the parser extracts the Ed25519 signature bytes and key ID
- **AND** the key ID matches the expected signing key

#### Scenario: Reject malformed signature file

- **WHEN** a `.sig` file with invalid format is provided (wrong line count,
  missing header, invalid base64)
- **THEN** the parser returns a failure result
- **AND** no signature bytes are produced

#### Scenario: Reject signature with wrong algorithm

- **WHEN** a `.sig` file specifies an algorithm other than Ed25519 (pure)
- **THEN** the parser returns a failure result indicating unsupported algorithm

### Requirement: Ed25519 signature verification

The system SHALL verify Ed25519 signatures against the manifest content using
the embedded public key. Verification SHALL use NSec.Cryptography (libsodium
wrapper) for Ed25519 support.

#### Scenario: Valid signature accepted

- **WHEN** a manifest and its corresponding valid `.sig` are provided
- **THEN** Ed25519 verification succeeds
- **AND** the manifest content is trusted

#### Scenario: Tampered manifest rejected

- **WHEN** a manifest has been modified after signing
- **THEN** Ed25519 verification fails
- **AND** the manifest content is rejected

#### Scenario: Wrong key rejected

- **WHEN** a valid signature was produced by a different signing key
- **THEN** verification fails because the key ID does not match the embedded
  public key

### Requirement: Embedded public key

The system SHALL embed the minisign public key as a compiled constant in the
binary. The key SHALL NOT be fetched from the CDN at runtime, because the CDN
is the untrusted channel being verified.

#### Scenario: Public key available without network

- **WHEN** signature verification is performed
- **THEN** the public key is read from the compiled binary
- **AND** no HTTP request is made to obtain the public key

#### Scenario: Public key matches release infrastructure

- **GIVEN** the CI pipeline signs with the private key corresponding to
  `feeds/releases/manifest.pub`
- **WHEN** the embedded public key is compared to the repo copy
- **THEN** they are identical

### Requirement: Fail-closed manifest verification

The system SHALL reject the manifest and abort the update when signature
verification fails. There SHALL be no fallback to unsigned manifest acceptance.

#### Scenario: Missing signature file aborts update

- **WHEN** `manifest.json.sig` cannot be downloaded (404, timeout, network error)
- **THEN** the manifest is rejected
- **AND** the update command exits with a non-zero code and an error message
  explaining the signature is missing

#### Scenario: Invalid signature aborts update

- **WHEN** the signature does not verify against the manifest content
- **THEN** the manifest is rejected
- **AND** the update command exits with a non-zero code and an error message
  warning of possible tampering

#### Scenario: Background check with signature failure

- **WHEN** a background update check encounters a signature verification failure
- **THEN** the check returns a "no update" result (same as network failure)
- **AND** the failure is logged at warning level
