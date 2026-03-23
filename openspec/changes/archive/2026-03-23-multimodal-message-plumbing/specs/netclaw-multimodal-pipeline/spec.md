## ADDED Requirements

### Requirement: AIContent pipeline transport

The message pipeline SHALL carry `AIContent[]` from channel input through to
the `IChatClient` call boundary. Non-text content items (images, file
references) SHALL NOT be silently discarded at any pipeline stage.

#### Scenario: Image content reaches vision model

- **GIVEN** a user sends a message with text and an attached image
- **AND** the configured model supports `InputModalities` including `Image`
- **WHEN** the message is processed through the pipeline
- **THEN** the `IChatClient` SHALL receive a `ChatMessage` containing both
  `TextContent` and `ImageContent` items

#### Scenario: Text-only message unchanged

- **GIVEN** a user sends a text-only message
- **WHEN** the message is processed through the pipeline
- **THEN** behavior SHALL be identical to the existing text-only pipeline

### Requirement: File-backed media persistence

Media content SHALL be persisted as file references (relative path + MIME type)
in `SerializableChatMessage`, not as inline binary data. File bytes SHALL be
read just-in-time at the `IChatClient` call boundary.

#### Scenario: Image persisted as file reference

- **GIVEN** a user message includes an image
- **WHEN** the message is persisted to the event journal
- **THEN** the `SerializableChatMessage` SHALL contain a `MediaReference` with
  the file's relative path and MIME type
- **AND** the journal entry SHALL NOT contain inline image bytes

#### Scenario: Missing file at recovery

- **GIVEN** a persisted message references a media file that no longer exists
- **WHEN** the session recovers from the journal
- **THEN** `ChatMessageConverter` SHALL log a warning and skip the missing media
- **AND** the text portion of the message SHALL be preserved
- **AND** session processing SHALL continue normally

### Requirement: Modality gate

The session actor SHALL compare inbound content modalities against
`SessionConfig.InputModalities` before processing. Unsupported content SHALL
be stripped with a user-facing acknowledgement.

#### Scenario: Image sent to text-only model

- **GIVEN** a model with `InputModalities` equal to `Text` only
- **WHEN** a user sends a message containing an image
- **THEN** the session actor SHALL strip the image content
- **AND** emit a `TextOutput` acknowledgement to subscribers explaining the
  model does not support image input
- **AND** continue processing the text portion of the message normally

#### Scenario: Image-only message to text-only model

- **GIVEN** a model with `InputModalities` equal to `Text` only
- **WHEN** a user sends a message containing only an image with no text
- **THEN** the session actor SHALL emit a `TextOutput` acknowledgement
- **AND** SHALL NOT invoke the LLM (no processable content)

#### Scenario: All content supported

- **GIVEN** a model with `InputModalities` including `Text` and `Image`
- **WHEN** a user sends a message with text and an image
- **THEN** all content SHALL pass through without acknowledgement

### Requirement: FileOutput broadcast

The session actor SHALL support emitting `FileOutput` events through the
subscriber broadcast for files the agent wants to share with the user.

#### Scenario: FileOutput delivered to subscribers

- **GIVEN** a subscriber with `OutputFilter` including `Files`
- **WHEN** the session actor emits a `FileOutput`
- **THEN** the subscriber SHALL receive the event containing the file path,
  file name, and MIME type

#### Scenario: FileOutput filtered when not subscribed

- **GIVEN** a subscriber with `OutputFilter` not including `Files`
- **WHEN** the session actor emits a `FileOutput`
- **THEN** the subscriber SHALL NOT receive the event
