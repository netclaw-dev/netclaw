## ADDED Requirements

### Requirement: Image proxy attachment retention

An adapter SHALL retain an accepted image when the main model accepts image input or a valid image proxy is configured.
The existing audience attachment policy, size limits, MIME checks, and media store rules SHALL remain authoritative.
The canonical attachment line SHALL identify proxy delivery with `inlined="true" via="image-proxy"`.

#### Scenario: Text-only main model has an image proxy

- **GIVEN** the attachment policy accepts an image
- **AND** the main model accepts text only
- **AND** an image proxy is configured
- **WHEN** an adapter processes the attachment
- **THEN** it SHALL retain the image as session media
- **AND** its canonical attachment line SHALL identify the image proxy route

#### Scenario: Text-only main model has no image proxy

- **GIVEN** the attachment policy accepts an image
- **AND** the main model accepts text only
- **AND** no image proxy is configured
- **WHEN** an adapter processes the attachment
- **THEN** the adapter SHALL keep the current path-only modality-gap result
