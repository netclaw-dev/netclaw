## ADDED Requirements

### Requirement: MIME and file-extension values are value-typed

The system SHALL represent MIME type and file-extension values with explicit
value objects for canonical MIME, declared MIME, verified MIME, and file
extension where those meanings affect policy, scanning, provider serialization,
or persistence. These value objects SHALL expose no implicit conversions to or
from primitive strings.

#### Scenario: Declared MIME cannot be passed as verified MIME

- **WHEN** code supplies a declared MIME value where a verified MIME value is
  required
- **THEN** the build fails with a type-mismatch error
- **AND** the caller must pass through content scanning to obtain verified MIME

#### Scenario: MIME value serializes as primitive string

- **GIVEN** a persisted media reference carries a MIME value object
- **WHEN** it is serialized through the protobuf mapping
- **THEN** the serialized MIME field remains the bare MIME string
- **AND** deserialization reconstructs the MIME value object
