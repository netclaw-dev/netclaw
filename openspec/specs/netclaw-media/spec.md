# netclaw-media Specification

## Purpose
TBD - created by archiving change unify-file-type-classification. Update Purpose after archive.
## Requirements
### Requirement: Shared media type catalog

The system SHALL provide a `Netclaw.Media` library that defines MIME and media
classification value objects and catalog metadata without depending on any other
Netclaw project. The catalog SHALL provide canonical MIME normalization,
alias handling, extension mapping, attachment category classification,
text/binary classification, model-input eligibility, and scanner-support
metadata for explicitly supported file types.

#### Scenario: Known alias resolves to canonical MIME

- **GIVEN** the raw MIME value `image/jpg`
- **WHEN** the media catalog normalizes the MIME value
- **THEN** the canonical MIME is `image/jpeg`

#### Scenario: Unknown image subtype is not privileged by prefix

- **GIVEN** the raw MIME value `image/x-unknown`
- **WHEN** the media catalog classifies the MIME value
- **THEN** the result is not classified as `Image`
- **AND** the value is treated as `Other` or unsupported until explicitly added
  to the catalog

### Requirement: Declared and verified MIME are distinct value types

The system SHALL represent MIME metadata declared by a transport separately from
MIME values verified by content scanning. A declared MIME value SHALL NOT be used
as proof that bytes match that type. A verified MIME value SHALL only be produced
after the scanner validates the file's bytes and filename against catalog-backed
rules.

#### Scenario: Declared MIME remains metadata before scan

- **GIVEN** an inbound attachment declared as `image/png`
- **WHEN** the channel applies the pre-download policy gate
- **THEN** the declared MIME can be used only for provisional category lookup
- **AND** the file still requires scanner verification before delivery

#### Scenario: Verified MIME drives downstream behavior

- **GIVEN** an inbound attachment is declared as `application/octet-stream`
- **AND** the filename and bytes validate as PNG
- **WHEN** the scanner accepts the file
- **THEN** downstream attachment formatting and model-input decisions use the
  verified MIME `image/png`

### Requirement: Native signature metadata supports security validation

The media catalog SHALL expose which canonical MIME types are supported by native
signature validation. The security scanner SHALL use this support metadata and
native matchers rather than an external runtime MIME detection package.

#### Scenario: Supported MIME set is available to scanner policy

- **WHEN** the content policy builds its default MIME allowlist
- **THEN** it uses the set of MIME types supported by native validation
- **AND** it does not duplicate a separate hardcoded scanner allowlist

#### Scenario: External MIME detector is not required

- **WHEN** Netclaw validates a supported PNG, PDF, archive, document, audio, or
  video signature
- **THEN** validation runs through native signature checks
- **AND** no `MimeDetective` runtime dependency is needed

