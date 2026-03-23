## MODIFIED Requirements

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit typed output events to
subscribers. Subscriber delivery SHALL use a direct subscription model with
`OutputFilter` bitmask so that subscribers control which output categories they
receive (Text, Thinking, ToolCalls, Usage, Files). Lifecycle events
(TurnCompleted, ErrorOutput, SessionTitleOutput) SHALL always be delivered
regardless of filter.

#### Scenario: Persist and emit assistant reply

- **WHEN** the assistant produces a response
- **THEN** a `TurnRecorded` event is persisted
- **AND** typed output events are emitted to subscribers based on their filter

#### Scenario: Multi-subscriber filtered delivery

- **GIVEN** multiple subscribers with different OutputFilter bitmasks
- **WHEN** a turn completes with text, thinking, usage data, and file attachments
- **THEN** each subscriber receives only the output categories matching their
  filter
- **AND** all subscribers receive lifecycle events regardless of filter

#### Scenario: FileOutput delivered to file-subscribed subscribers

- **GIVEN** a subscriber with `OutputFilter` including `Files`
- **WHEN** the session actor emits a `FileOutput`
- **THEN** the subscriber SHALL receive the event containing the file path,
  file name, and MIME type

## ADDED Requirements

### Requirement: Multimodal message persistence

`SerializableChatMessage` SHALL support persisting media file references
alongside text content. Media SHALL be stored as relative file paths with MIME
type metadata, not as inline binary data.

#### Scenario: User message with image persisted

- **GIVEN** a user sends a message with text and an attached image
- **WHEN** the message is persisted to the event journal
- **THEN** the `SerializableChatMessage` SHALL contain the text in `Content`
- **AND** SHALL contain a `MediaReference` with the image file's relative path
  and MIME type

#### Scenario: Chat message converter reconstructs image content

- **GIVEN** a persisted `SerializableChatMessage` contains media references
- **WHEN** `ChatMessageConverter.ToAiMessage` converts it for the LLM call
- **THEN** the converter SHALL read the file bytes from disk
- **AND** construct MEAI `ImageContent` with the binary data
- **AND** include both `TextContent` and `ImageContent` in the resulting
  `ChatMessage`

#### Scenario: Missing media file handled gracefully

- **GIVEN** a persisted message references a media file that no longer exists
- **WHEN** `ChatMessageConverter.ToAiMessage` attempts to read the file
- **THEN** the converter SHALL log a warning
- **AND** skip the missing media reference
- **AND** include only the text content in the `ChatMessage`

### Requirement: Modality gate

The session actor SHALL compare inbound content modalities against
`SessionConfig.InputModalities` before adding content to session state.
Unsupported content SHALL be stripped with a user-facing acknowledgement
emitted to subscribers.

#### Scenario: Image sent to text-only model

- **GIVEN** a model with `InputModalities` equal to `Text` only
- **WHEN** a user sends a message containing text and an image
- **THEN** the session actor SHALL strip the image content
- **AND** emit a `TextOutput` acknowledgement explaining the model does not
  support image input
- **AND** continue processing the text portion normally

#### Scenario: Image-only message to text-only model

- **GIVEN** a model with `InputModalities` equal to `Text` only
- **WHEN** a user sends a message containing only an image with no text
- **THEN** the session actor SHALL emit a `TextOutput` acknowledgement
- **AND** SHALL NOT invoke the LLM

#### Scenario: All content supported passes through

- **GIVEN** a model with `InputModalities` including `Text` and `Image`
- **WHEN** a user sends a message with text and an image
- **THEN** all content SHALL pass through without acknowledgement
- **AND** the LLM SHALL receive both text and image content
