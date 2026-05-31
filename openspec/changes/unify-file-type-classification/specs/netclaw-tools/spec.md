## ADDED Requirements

### Requirement: File tools use typed MIME values

File-related tool side channels SHALL carry typed MIME values rather than raw
strings. Tool registrations for file attachments and model-input files SHALL
store canonical MIME values from the shared media catalog while preserving the
existing string wire format when serialized or displayed.

#### Scenario: Model input file carries canonical MIME

- **GIVEN** a tool registers a model-input file with MIME alias `image/jpg`
- **WHEN** the tool execution context records the file
- **THEN** the stored MIME value is canonical `image/jpeg`

#### Scenario: File attachment display preserves MIME string shape

- **GIVEN** a tool registers a file attachment with a typed MIME value
- **WHEN** the attachment is emitted as user-visible output
- **THEN** the MIME is displayed as the canonical MIME string

### Requirement: Model-input media eligibility is catalog-backed

The tool execution pipeline SHALL decide whether a file can be attached to the
next model request using the shared media catalog's model-input eligibility and
the active model's input modalities. The pipeline SHALL NOT rely on ad hoc MIME
prefix checks.

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

### Requirement: Web fetch MIME decisions use shared media catalog

The `web_fetch` tool SHALL use the shared media catalog for content-type
normalization, binary/text classification, and fallback extension selection.
It SHALL NOT maintain a separate MIME-to-extension table for types already
present in the media catalog.

#### Scenario: Binary fetch extension comes from catalog

- **GIVEN** an HTTP response with content type `application/pdf`
- **AND** the URL path does not include a usable extension
- **WHEN** `web_fetch` saves the response
- **THEN** it chooses `.pdf` from the media catalog
