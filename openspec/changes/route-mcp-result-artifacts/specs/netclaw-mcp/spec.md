## ADDED Requirements

### Requirement: MCP result artifacts pass content verification

The system SHALL treat MCP result names and MIME values as untrusted metadata. It SHALL verify artifact bytes before any file write or output registration.

The verified MIME SHALL select the stored extension and the MIME value on each registered output.

#### Scenario: Valid image passes verification

- **GIVEN** an MCP tool returns a valid PNG with declared MIME `image/png`
- **WHEN** Netclaw processes the tool result
- **THEN** the existing content scanner verifies the PNG bytes
- **AND** Netclaw stores the artifact with verified MIME `image/png`

#### Scenario: Declared image contains different bytes

- **GIVEN** an MCP tool declares `image/png` for bytes that are not a valid PNG
- **WHEN** Netclaw processes the tool result
- **THEN** Netclaw writes no artifact file for those bytes
- **AND** Netclaw registers no user file or model input for those bytes
- **AND** the model result contains a visible rejection note

#### Scenario: Server filename cannot select the final path

- **GIVEN** an MCP artifact has a path-shaped or misleading filename
- **WHEN** the scanner accepts its bytes
- **THEN** Netclaw creates the file below the current session artifact directory
- **AND** the verified MIME selects the final extension

### Requirement: Verified MCP artifacts use existing tool outputs

The system SHALL register every verified MCP result artifact as an existing user file output. It SHALL also register eligible artifacts as model input when the active model supports the required modality.

The system SHALL use the existing tool-output, model-input, session-media, and channel delivery paths. It SHALL NOT add an MCP-specific actor message or channel delivery path.

#### Scenario: Image-capable model receives a verified image

- **GIVEN** an MCP tool returns a verified PNG
- **AND** the active model supports image input
- **WHEN** Netclaw completes the MCP tool call
- **THEN** the user receives the PNG through the normal file-output path
- **AND** the next model request receives the PNG through the normal model-input path

#### Scenario: Text-only model preserves a verified image

- **GIVEN** an MCP tool returns a verified PNG
- **AND** the active model does not support image input
- **WHEN** Netclaw completes the MCP tool call
- **THEN** the user receives the PNG through the normal file-output path
- **AND** the next model request receives no image reference for that PNG
- **AND** the model result states that the current model cannot inspect the artifact

#### Scenario: Verified format is not eligible for model input

- **GIVEN** an MCP tool returns a verified artifact that the media catalog does not permit as model input
- **WHEN** Netclaw completes the MCP tool call
- **THEN** the user receives the artifact through the normal file-output path
- **AND** the provider receives no incompatible media

### Requirement: MCP artifact failures preserve safe partial results

The system SHALL preserve readable text when one MCP artifact fails admission. It SHALL bound artifact count and aggregate bytes before scan or file work.

Caller cancellation SHALL remove files created by that invocation and SHALL register no pending artifact outputs.

#### Scenario: One artifact fails and text remains

- **GIVEN** an MCP result contains readable text and an invalid artifact
- **WHEN** Netclaw processes the result
- **THEN** the readable text remains in its original order
- **AND** the result includes a visible artifact rejection note
- **AND** no binary data appears in model text

#### Scenario: Result exceeds artifact bounds

- **GIVEN** an MCP result exceeds the artifact count or aggregate-byte limit
- **WHEN** Netclaw processes the result
- **THEN** Netclaw rejects each artifact outside the bound before scan or file work
- **AND** the result contains a visible bound-rejection note

#### Scenario: Session storage is unavailable

- **GIVEN** an MCP tool call has no bound session storage
- **WHEN** its result contains an artifact
- **THEN** Netclaw writes no artifact file
- **AND** Netclaw registers no user file or model input
- **AND** the result contains a visible storage-unavailable note

#### Scenario: Caller cancels artifact work

- **GIVEN** MCP artifact work has not registered its outputs
- **WHEN** the caller cancels the tool call
- **THEN** Netclaw registers no artifact output from that call
- **AND** Netclaw removes files that the call created

#### Scenario: Tool-declared error contains data

- **GIVEN** an MCP server marks a tool result as an error
- **AND** the error result contains a data block
- **WHEN** Netclaw processes the result
- **THEN** Netclaw preserves the existing attributed error and failure receipt
- **AND** Netclaw registers no artifact from the error result
