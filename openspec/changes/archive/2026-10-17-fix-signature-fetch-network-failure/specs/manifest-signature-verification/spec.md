## MODIFIED Requirements

### Requirement: Fail-closed manifest verification

The system SHALL reject the manifest and abort the update when signature
verification fails. There SHALL be no fallback to unsigned manifest acceptance.

#### Scenario: Missing signature file aborts update

- **WHEN** `manifest.json.sig` cannot be downloaded because the server returns
  an HTTP error status (for example 404)
- **THEN** the manifest is rejected
- **AND** the update command exits with a non-zero code and an error message
  explaining the signature is missing

#### Scenario: Signature fetch times out or fails to connect

- **WHEN** the download of `manifest.json.sig` times out or fails with a
  transport error and the server sends no HTTP response
- **THEN** the manifest is rejected
- **AND** the update command exits with a non-zero code
- **AND** the error message names the network problem and does not warn of
  tampering

#### Scenario: Invalid signature aborts update

- **WHEN** the signature does not verify against the manifest content
- **THEN** the manifest is rejected
- **AND** the update command exits with a non-zero code and an error message
  warning of possible tampering

#### Scenario: Background check with signature failure

- **WHEN** a background update check encounters a signature verification failure
- **THEN** the check returns a "no update" result (same as network failure)
- **AND** the failure is logged at warning level
