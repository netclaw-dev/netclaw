# bounded-image-egress Specification

## Purpose

Define the bounded image egress pipeline: downscaling, memory-bounded decode,
single-boundary normalization, and failure semantics for images attached to
model requests.

## Requirements

### Requirement: Model-bound images are downscaled to a bounded payload

Before an image is serialized into a model request, the system SHALL downscale
it to a bounded payload using a shared image normalizer: the output SHALL have
its longest edge no greater than the long-edge cap (~1568px, preserving aspect
ratio) AND its encoded base64 size no greater than the byte budget (~5MB). Images
already within both bounds SHALL be passed through without upscaling and without
unnecessary re-encoding.

#### Scenario: Image larger than the long-edge cap is downscaled

- **GIVEN** an image whose longest edge is 8000px
- **AND** a configured long-edge cap of 1568px
- **WHEN** the normalizer processes the image
- **THEN** the output's longest edge is no greater than 1568px
- **AND** the output aspect ratio matches the source within rounding

#### Scenario: Image exceeding the byte budget is shrunk under it

- **GIVEN** an image whose encoded base64 size exceeds the byte budget
- **WHEN** the normalizer processes the image
- **THEN** the output's encoded base64 size is no greater than the byte budget
- **AND** the shrink procedure terminates without unbounded iteration

#### Scenario: Image already within bounds is not upscaled

- **GIVEN** an image whose longest edge and encoded size are both within the caps
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

### Requirement: Images are normalized once at the session media-store boundary

The system SHALL normalize an image once, at the session media-store write
boundary, so the persisted media artifact is already bounded. Every image reaches
the model through the session media store — chat attachments and persisted
message media via `WriteDataContent`, and `file_read` model-input handoffs via
`CopyFile` — so this single boundary covers both origins. The egress read path
SHALL read the persisted artifact unchanged and SHALL NOT re-normalize per turn.
There SHALL be no separate per-turn encode cache: because the media store
persists the normalized artifact once, every later turn reads the already-bounded
bytes. Non-image media SHALL pass through the boundary unchanged.

#### Scenario: Chat attachment is normalized once on write

- **GIVEN** an oversized image is admitted as a chat attachment
- **WHEN** it is written to the session media store
- **THEN** the stored artifact is already within the caps
- **AND** later egress reads of that artifact do not re-run the normalizer

#### Scenario: file_read image is normalized once when copied into media

- **GIVEN** a `file_read` model-input handoff for an oversized image
- **WHEN** the image is copied into the session media store
- **THEN** the stored artifact is already within the caps
- **AND** every later turn reads the bounded artifact rather than the original file

#### Scenario: Non-image media is not altered

- **GIVEN** a non-image file (e.g. a PDF) written to the session media store
- **WHEN** it crosses the media-store write boundary
- **THEN** its bytes are stored unchanged

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

- **GIVEN** an image that cannot be reduced under the byte budget
- **WHEN** the normalizer processes it for model input
- **THEN** the image is dropped with a visible note
- **AND** the original unbounded bytes are not attached to the model request

### Requirement: Image-egress bounds are fixed memory-safe constants

The image-egress bounds (long-edge cap, byte budget, encode quality) SHALL be
fixed constants chosen for memory safety, defaulting to ≈1568px long edge and a
≈5MB base64 budget. They SHALL NOT be raisable through runtime configuration:
because the byte budget is the lever that bounds peak memory, exposing it as a
user-raisable setting would let a misconfiguration re-introduce the unbounded
condition this change exists to remove. No `netclaw-config.v1.schema.json` change
is required.

#### Scenario: Bounds cannot be weakened by configuration

- **GIVEN** a deployment with any configuration
- **WHEN** an image is normalized
- **THEN** the long-edge cap and byte budget applied are the fixed safe constants
- **AND** there is no configuration path that raises the byte budget above the constant
