## Why

Source PRDs: PRD-001

Netclaw now detects model modality capabilities (Text, Image, Audio, Video) but
the message pipeline is string-only end-to-end. Images sent by users in Slack
are silently discarded at `ChannelPipeline.MapToCommand`. Tool-produced files
(Playwright screenshots, downloaded documents) can't be shared back to users.
The agent has no way to send or receive non-text content despite using
vision-capable models.

## What Changes

- **BREAKING**: `SendUserMessage.Content` changes from `string` to carry
  `AIContent[]` so non-text content survives the pipeline from channel input to
  session actor
- **BREAKING**: `SerializableChatMessage` gains a new Protobuf field for media
  file references alongside existing `Content` string, enabling persistence of
  multimodal turns
- `ChannelPipeline.MapToCommand` passes through all `AIContent` types instead of
  filtering to `TextContent` only
- `ChatMessageConverter` reconstructs `ImageContent` (and future media types)
  from persisted file references at the `IChatClient` boundary
- New **modality gate** in the session actor: when a user sends content the
  configured model doesn't support (e.g., image to a text-only model), emit a
  user-facing acknowledgement instead of silently dropping it
- New **`attach_file` first-party tool**: the agent calls this to explicitly
  attach a file from the session directory to its output. Channel adapters
  render appropriately (Slack: `files.uploadV2`, TUI: print local file path)
- **Slack inbound adapter** downloads file attachments from Slack's API and
  writes them to the session-scoped temp directory, producing file-backed
  `ImageContent` in the `ChannelInput`
- New **`FileOutput` session output type**: carries a file path and MIME type
  through the broadcast subscription to channel adapters

### Content model

All media normalizes to **files on disk** in the session-scoped temp directory
(`/tmp/netclaw-sessions/{sessionId}/`). The pipeline carries file path
references, not binary data. At the `IChatClient` call boundary, file bytes are
read just-in-time to construct MEAI `ImageContent`. This keeps the persistence
journal small, provides a natural security boundary (file paths validated to
session directory), and gives a consistent model for both inbound (user sends
image) and outbound (tool produces screenshot) flows.

File references are ephemeral — they don't survive `/tmp` cleanup. The agent's
text-based reasoning about the content remains in the persisted conversation
history.

### Modality gate

When the session actor receives content types not supported by the model's
`InputModalities`, it:

1. Strips the unsupported content from the message
2. Emits an acknowledgement to the user (e.g., "This model doesn't support
   image input — I can only see the text portion of your message")
3. Continues processing any supported content normally

### attach_file tool

The agent decides when to share files with the user — tools stay text-in/text-out.
When a tool writes a file (e.g., Playwright screenshot), the tool result text
mentions the path. The agent sees this and calls `attach_file(path)` to
explicitly attach it to the output. The tool validates the path is within the
session directory, reads MIME type, and produces a `FileOutput` event in the
broadcast. Channel adapters render per their capabilities.

### Channel adapter rendering

Each channel adapter renders `FileOutput` according to its capabilities:

- **Slack**: calls `files.uploadV2` to attach the file to the thread
- **TUI**: prints the local file path (potentially as a clickable `file://` URI)
- **Future web UI**: could inline the image or serve from a local endpoint

The agent doesn't know or care which channel it's talking to.

## Capabilities

### New Capabilities

- `netclaw-multimodal-pipeline`: Multimodal content flow through the message
  pipeline — `AIContent[]` transport, file-backed media persistence, modality
  gate, and `FileOutput` broadcast type
- `netclaw-file-attachment`: `attach_file` first-party tool for agent-initiated
  file sharing with channel-adapter-specific rendering

### Modified Capabilities

- `netclaw-session`: Session actor gains modality gate behavior and `FileOutput`
  emission; `SerializableChatMessage` gains media file reference field;
  `ChatMessageConverter` handles multimodal content reconstruction
- `netclaw-slack-socket`: Slack adapter gains file attachment download (inbound)
  and `files.uploadV2` upload (outbound via `FileOutput`)
- `netclaw-input-adapters`: `SendUserMessage` changes from string to `AIContent[]`
  content; channel pipeline passes through non-text content
- `netclaw-tools`: Tool registry gains `attach_file` first-party tool definition

## In Scope (MVP)

- Image input (user → vision model) via Slack file attachments
- Image/file output (agent → user) via `attach_file` tool
- Modality gate with user-facing acknowledgement
- Slack and TUI channel adapter support
- File-backed persistence model

## Out of Scope

- Audio/video content handling (future — modality flags support it, pipeline doesn't yet)
- LLM-synthesized images (DALL-E style output modalities)
- Cross-session file sharing or permanent file storage
- File content in context compaction summaries (text summary only)

## Impact

- **Persistence format**: `SerializableChatMessage` Protobuf schema changes
  (additive — new field for file references). Existing journals remain compatible.
- **Protocol**: `SendUserMessage` content type changes from `string` to
  `AIContent[]`. All channel adapters and tests that construct this message must
  update.
- **Session output**: New `FileOutput` type added to `SessionOutput` hierarchy
  and `OutputFilter` flags.
- **Dependencies**: Slack file download requires authenticated HTTP calls using
  the bot token (already available in Slack config).
- **Security**: `attach_file` validates file paths are within session directory
  to prevent path traversal. Slack file downloads validated against allowed
  channels/users per existing ACL.
- **Disk usage**: Session temp directories accumulate media files. Existing
  cleanup-on-session-end policy applies.
