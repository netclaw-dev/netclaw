## ADDED Requirements

### Requirement: Model-bound images are downscaled to a bounded payload

Before an image is serialized into a model request, the system SHALL downscale
it to a bounded payload using a shared image normalizer: the output SHALL have
its longest edge no greater than the configured long-edge cap (default ~1568px,
preserving aspect ratio) AND its encoded base64 size no greater than the
configured byte budget (default ~5MB). Images already within both bounds SHALL
be passed through without upscaling and without unnecessary re-encoding.

#### Scenario: Image larger than the long-edge cap is downscaled

- **GIVEN** an image whose longest edge is 8000px
- **AND** a configured long-edge cap of 1568px
- **WHEN** the normalizer processes the image
- **THEN** the output's longest edge is no greater than 1568px
- **AND** the output aspect ratio matches the source within rounding

#### Scenario: Image exceeding the byte budget is shrunk under it

- **GIVEN** an image whose encoded base64 size exceeds the configured byte budget
- **WHEN** the normalizer processes the image
- **THEN** the output's encoded base64 size is no greater than the byte budget
- **AND** the shrink procedure terminates without unbounded iteration

#### Scenario: Image already within bounds is not upscaled

- **GIVEN** an image whose longest edge and encoded size are both within the
  configured caps
- **WHEN** the normalizer processes the image
- **THEN** the output dimensions are not larger than the source dimensions

### Requirement: Image decode is memory-bounded

The normalizer SHALL bound peak decode memory by the downscaled target size, not
the source resolution: it SHALL down-sample while decoding (e.g. codec
sample-size selection) so that a full-resolution bitmap of an oversized source is
never materialized. The decode sample-size decision SHALL be a deterministic
function of source dimensions and the long-edge cap.

#### Scenario: Oversized source does not materialize a full-resolution bitmap

- **GIVEN** an 8000x8000 source image
- **AND** a configured long-edge cap of 1568px
- **WHEN** the normalizer decodes the image
- **THEN** the decoded bitmap dimensions are bounded near the target (about
  2000px or smaller on the longest edge), not 8000px
- **AND** peak decode allocation stays within a bound derived from the target
  size rather than the source size

#### Scenario: Sample-size selection is deterministic

- **GIVEN** fixed source dimensions and a fixed long-edge cap
- **WHEN** the decode sample-size is computed
- **THEN** the same sample-size is returned on every call for those inputs

### Requirement: Encoded image is produced at most once per distinct image

The system SHALL avoid re-encoding the same image on every turn. Chat attachment
images SHALL be normalized once at ingestion and the bounded artifact SHALL be
persisted so that later turns read an already-bounded file. Images handed off
from `file_read` SHALL be normalized at egress and the encoded result SHALL be
cached keyed by image content hash, so that repeated turns referencing the same
image reuse the cached result rather than re-decoding and re-encoding.

#### Scenario: Chat attachment is normalized once at ingestion

- **GIVEN** an oversized image is admitted as a chat attachment
- **WHEN** the attachment is written to the session media store
- **THEN** the stored artifact is already within the configured caps
- **AND** later egress reads of that artifact do not re-run the normalizer

#### Scenario: Repeated file_read image reuses the cached encoding

- **GIVEN** a `file_read` image handoff has been normalized and cached
- **WHEN** the same image bytes are referenced again on a later turn
- **THEN** the cached encoded result is reused
- **AND** the normalizer is not invoked a second time for those bytes

#### Scenario: Distinct images are cached separately

- **GIVEN** two `file_read` images with different content
- **WHEN** both are handed off for model input
- **THEN** each is normalized and cached under its own content hash

### Requirement: Un-shrinkable or undecodable images fail loud

The system SHALL NOT silently pass through an image it cannot bound. An image
that cannot be decoded, or that cannot be shrunk under the byte budget, SHALL be
dropped and replaced with a visible `[image omitted: <reason>]` note in the
message content. The system SHALL NOT attach raw, unbounded image bytes to a
model request as a fallback.

#### Scenario: Undecodable image is dropped with a note

- **GIVEN** a file whose bytes cannot be decoded as a supported image
- **WHEN** the normalizer processes it for model input
- **THEN** no image content is attached to the model request
- **AND** a visible `[image omitted: ...]` note is present in the message content

#### Scenario: Un-shrinkable image is dropped, not shipped raw

- **GIVEN** an image that cannot be reduced under the configured byte budget
- **WHEN** the normalizer processes it for model input
- **THEN** the image is dropped with a visible note
- **AND** the original unbounded bytes are not attached to the model request

### Requirement: Image-egress caps are configurable

The system SHALL expose the image-egress bounds (long-edge cap, byte budget,
encode quality, and an enable/disable switch) as configuration in
`Netclaw.Configuration`. The configuration schema
(`netclaw-config.v1.schema.json`) SHALL define these properties with safe
defaults so that existing configurations validate and upgrade without manual
edits.

#### Scenario: Configured caps are honored

- **GIVEN** a configuration setting a long-edge cap and byte budget
- **WHEN** an image is normalized
- **THEN** the output respects the configured cap and budget rather than
  hard-coded values

#### Scenario: Existing configuration validates against the schema

- **GIVEN** a configuration that omits the image-egress block
- **WHEN** the configuration is loaded and schema-validated
- **THEN** validation succeeds using the schema defaults
- **AND** no schema `additionalProperties` violation is raised
