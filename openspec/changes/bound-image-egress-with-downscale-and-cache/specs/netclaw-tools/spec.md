## MODIFIED Requirements

### Requirement: Model-input media eligibility is catalog-backed

The tool execution pipeline SHALL decide whether a file can be attached to the
next model request using the shared media catalog's model-input eligibility and
the active model's input modalities. The pipeline SHALL NOT rely on ad hoc MIME
prefix checks.

When an eligible image is materialized into session media (`CopyFile`), the bytes
SHALL pass through the bounded image normalizer (see `bounded-image-egress`) so
the stored media artifact is already bounded; later turns read that artifact, so
no separate per-turn cache is needed. The pipeline SHALL NOT attach the raw
on-disk image bytes directly to the model request, and SHALL drop (with a visible
note) any image the normalizer cannot bound rather than attaching unbounded bytes.

#### Scenario: Supported image is attached only for image-capable model

- **GIVEN** a PNG file has verified MIME `image/png`
- **AND** the active model supports image input
- **WHEN** the tool execution pipeline materializes model-input files
- **THEN** the image is copied into session media and attached to the next model
  request

#### Scenario: Unsupported media is skipped before provider serialization

- **GIVEN** a model-input file has MIME `audio/mpeg`
- **WHEN** the tool execution pipeline materializes model-input files
- **THEN** the file is not attached as model input
- **AND** the provider does not receive non-image `DataContent` through the
  image-only OpenAI-compatible path

#### Scenario: Attached image is bounded at materialization

- **GIVEN** an oversized PNG file eligible for image model input
- **WHEN** the tool execution pipeline materializes the model-input file
- **THEN** the image copied into session media is bounded by the image-egress caps
- **AND** later turns read the bounded media artifact rather than re-normalizing
