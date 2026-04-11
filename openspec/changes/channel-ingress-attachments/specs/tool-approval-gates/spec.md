## ADDED Requirements

### Requirement: Channel attachment policy per audience

The system SHALL support per-audience inbound channel attachment
policy via a `ChannelAttachments` field on `ToolAudienceProfile`,
extending the same per-audience configuration container that already
holds `ToolApprovalConfig`, file-access profiles, and tool grants.
`ChannelAttachments` SHALL specify:

- `AllowedCategories` — a set of `AttachmentCategory` enum values
  (`Image`, `Pdf`, `Document`, `Archive`, `Media`, `Other`) naming
  which classes of inbound files a channel adapter is permitted to
  deliver to the session for this audience.
- `MaxFileBytes` — a per-file size cap, applied by channel adapters
  against the transport-reported size before download.
- `MaxFilesPerMessage` — a per-inbound-message attachment-count cap,
  applied by channel adapters before download.

Channel adapters SHALL read these values through the resolved
`ToolAudienceProfile` for the inbound message's `TrustAudience` and
SHALL NOT maintain a parallel attachment policy surface. MIME types
SHALL be mapped to `AttachmentCategory` via a single internal
mapping function so that adding coverage for a new MIME type is a
one-place change. Unknown or unrecognized MIME types SHALL map to
`AttachmentCategory.Other`.

Default `AllowedCategories` per audience SHALL be:

- `Public`: `{ Image }`. Documents, PDFs, archives, media, and
  unknown binaries are rejected by default because processing them
  typically routes through tool-based execution on user-controlled
  bytes in a context where any workspace member can upload.
- `Team`: `{ Image, Pdf, Document, Archive, Media }`. All well-known
  categories except unknown binaries.
- `Personal`: `{ Image, Pdf, Document, Archive, Media, Other }`. All
  categories including unknown MIME types.

Default `MaxFileBytes` SHALL be 25 × 1024 × 1024 (25 MiB) for every
audience. Default `MaxFilesPerMessage` SHALL be 10 for every
audience. Operators SHALL be able to override any cell via
configuration; overrides SHALL be validated at startup against
`netclaw-config.v1.schema.json`.

#### Scenario: Default Public profile rejects a PDF category

- **GIVEN** a session resolves to `TrustAudience.Public`
- **AND** the operator has not overridden `ChannelAttachments` for
  the `Public` profile
- **WHEN** a channel adapter evaluates a file with MIME
  `application/pdf`
- **THEN** the `Pdf` category is not in the resolved profile's
  `AllowedCategories`
- **AND** the adapter rejects the file per the cross-channel contract

#### Scenario: Default Team profile accepts a Word document

- **GIVEN** a session resolves to `TrustAudience.Team`
- **WHEN** a channel adapter evaluates a file with MIME
  `application/vnd.openxmlformats-officedocument.wordprocessingml.document`
- **THEN** the file maps to `AttachmentCategory.Document`
- **AND** the `Document` category is in the resolved profile's
  default `AllowedCategories`
- **AND** the adapter proceeds to size and scan checks

#### Scenario: Unknown MIME type maps to Other and is rejected in Team

- **GIVEN** a session resolves to `TrustAudience.Team`
- **WHEN** a channel adapter evaluates a file with an unrecognized
  MIME type
- **THEN** the file is mapped to `AttachmentCategory.Other`
- **AND** the default `Team` profile does NOT allow `Other`
- **AND** the adapter rejects the file

#### Scenario: Unknown MIME type is accepted in Personal

- **GIVEN** a session resolves to `TrustAudience.Personal`
- **WHEN** a channel adapter evaluates a file with an unrecognized
  MIME type
- **THEN** the file is mapped to `AttachmentCategory.Other`
- **AND** the default `Personal` profile allows `Other`
- **AND** the adapter proceeds to size and scan checks

#### Scenario: Operator override widens Public to allow images and PDFs

- **GIVEN** the operator has configured
  `ChannelAttachments.AllowedCategories = { Image, Pdf }` on the
  `Public` profile
- **AND** `netclaw doctor` validates the config on startup
- **WHEN** a channel adapter evaluates a PDF in a public channel
- **THEN** the `Pdf` category is permitted
- **AND** the adapter proceeds to size and scan checks

#### Scenario: MaxFileBytes default is 25 MiB

- **GIVEN** no operator override for `MaxFileBytes`
- **WHEN** `ChannelAttachments` is materialized from defaults for any
  audience
- **THEN** `MaxFileBytes` equals 25 × 1024 × 1024

#### Scenario: MaxFilesPerMessage default is 10

- **GIVEN** no operator override for `MaxFilesPerMessage`
- **WHEN** `ChannelAttachments` is materialized from defaults for any
  audience
- **THEN** `MaxFilesPerMessage` equals 10

#### Scenario: Schema migration inserts defaults for stale configs

- **GIVEN** an existing config without a `ChannelAttachments` block
- **WHEN** `netclaw doctor --fix` runs against the config
- **THEN** the schema fix resolver inserts the default
  `ChannelAttachments` block for each audience profile
- **AND** the fixed config validates against
  `netclaw-config.v1.schema.json`
