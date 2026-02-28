## Context

Netclaw's message pipeline is currently string-only end-to-end despite the
`ChannelInput` abstraction already accepting `IReadOnlyList<AIContent>`. The
previous change (`multimodal-model-capabilities`) added capability detection,
so we now know whether a model supports image input. This change wires the
actual content through.

Key pipeline components and their current state:

- `ChannelInput.Contents` — `IReadOnlyList<AIContent>`, already multimodal-ready
- `ChannelPipeline.MapToCommand` — extracts `TextContent` only, drops everything else
- `SendUserMessage.Content` — `string` (Protobuf-persisted)
- `SerializableChatMessage.Content` — `string` (Protobuf-persisted, event-sourced)
- `ChatMessageConverter` — handles Text, FunctionCall, FunctionResult only
- `SessionOutput` — text-only output types
- `OutputFilter` — no file/media category

## Goals / Non-Goals

**Goals:**

- `AIContent[]` flows from `ChannelInput` through to `IChatClient` without
  lossy string conversion in the middle
- Images sent by users in Slack reach vision-capable models as `ImageContent`
- Models that don't support non-text input emit a user-facing acknowledgement
- The agent can attach files to its output via an `attach_file` tool
- Channel adapters render file output per their capabilities (Slack upload vs
  TUI file path)
- Persistence journal stays compact — file references, not inline binary

**Non-Goals:**

- Audio/video pipeline wiring (modality flags exist, pipeline work deferred)
- LLM-synthesized image output (DALL-E style)
- Permanent file storage or cross-session file sharing
- Image content in compaction summaries (text summary only)

## Decisions

### 1. File-backed media model

**Decision:** All media normalizes to files on disk in the session-scoped temp
directory. The pipeline carries relative file paths, not binary data.

**Alternatives considered:**

- *Inline base64 in Protobuf*: Simpler round-trip but bloats journal. A 2MB
  screenshot per turn adds up fast in event-sourced persistence. Rejected.
- *Content-addressed blob store*: Good for deduplication but over-engineered for
  MVP single-process host. Can migrate to this later if needed.

**How it works:**

- Inbound (Slack): adapter downloads attachment → writes to
  `{sessionDir}/media/{guid}.{ext}` → produces `ChannelInput` with file path
- Outbound (attach_file): file already in session dir → tool validates path →
  emits `FileOutput`
- LLM call boundary: `ChatMessageConverter.ToAiMessage` reads file bytes
  just-in-time to construct MEAI `ImageContent` with `DataContent` property
- Persistence: `SerializableChatMessage` stores a list of `MediaReference`
  records (relative path + MIME type + modality) alongside existing `Content`
  string

File references are ephemeral — they don't survive `/tmp` cleanup. The agent's
text reasoning about the content persists in conversation history.

### 2. SendUserMessage carries AIContent[]

**Decision:** Add a `Contents` property (`List<SerializableContent>`) to
`SendUserMessage` alongside the existing `Content` string. The string field
remains for backward compatibility and carries the text portion. The new field
carries media references.

**Why not replace Content entirely:** `SendUserMessage` is Protobuf-serialized
and persisted via event sourcing. Existing journals contain string `Content`.
Adding a new field (additive Protobuf change) is forward-compatible; removing
the string field would break recovery of existing sessions.

### 3. SerializableChatMessage gains MediaReferences

**Decision:** Add a `List<SerializableMediaReference>` field to
`SerializableChatMessage` with Protobuf tag 6. Each reference carries:

- `RelativePath` (string) — path relative to session directory
- `MimeType` (string) — e.g., `image/png`, `image/jpeg`
- `Modality` (int) — maps to `ModelModality` enum value

`ChatMessageConverter.ToAiMessage` reads the file at the absolute path
(session dir + relative path) and constructs the appropriate MEAI content type.
If the file is missing (e.g., after `/tmp` cleanup), it logs a warning and
skips the media — the text portion of the message still works.

### 4. Modality gate in session actor

**Decision:** The modality gate runs in `LlmSessionActor` when processing
`SendUserMessage`, before adding to session state. It compares each content
item's modality against `SessionConfig.InputModalities`.

Behavior:

1. Separate content into supported and unsupported items
2. If unsupported items exist, emit a `TextOutput` acknowledgement to
   subscribers (e.g., "This model doesn't support image input — I can only
   see the text portion of your message")
3. Continue processing with supported items only
4. If ALL content is unsupported (e.g., image-only message to text-only model),
   emit acknowledgement and skip the LLM call entirely

The gate does not throw or error — it's informational. The session continues
normally with whatever content the model can handle.

### 5. attach_file tool

**Decision:** `attach_file` is a first-party tool registered in `ToolRegistry`.
It takes a `path` parameter (string), validates the path is within the session
directory (path traversal prevention), reads the file metadata, and returns a
text confirmation to the LLM. As a side effect, it emits a `FileOutput` event
through the session's broadcast.

**Why a tool, not infrastructure magic:** The agent decides what to share. Tools
stay text-in/text-out. When a tool (e.g., Playwright screenshot) writes a file
and returns text mentioning the path, the agent sees this and deliberately calls
`attach_file` if it wants the user to see the file. This keeps the agent in
control.

**Implementation:** The tool needs access to the session's subscriber broadcast
to emit `FileOutput`. This is provided via `ToolExecutionContext` which already
carries the session ID. The tool emits `FileOutput` through the session actor
(Tell), which broadcasts to subscribers.

### 6. FileOutput and OutputFilter.Files

**Decision:** Add `FileOutput` as a new `SessionOutput` subtype:

```csharp
public sealed record FileOutput : SessionOutput
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required string MimeType { get; init; }
}
```

Add `Files = 1 << 4` to `OutputFilter`. Update `Full` preset to include it.
Channel adapters handle `FileOutput`:

- Slack: reads file, calls `files.uploadV2` to attach to thread
- TUI: prints file path (e.g., `[File: /tmp/.../screenshot.png]`)

### 7. Slack inbound file download

**Decision:** When the Slack adapter receives a message with a `files` array in
the event payload, it downloads each file using the bot token for
authentication (`Authorization: Bearer xoxb-...`). Files are written to the
session media directory and included as media content in `ChannelInput`.

Only supported MIME types are downloaded:

- `image/png`, `image/jpeg`, `image/gif`, `image/webp` — mapped to
  `ModelModality.Image`

Unsupported file types (PDFs, ZIPs, etc.) are skipped with a debug log. Future
changes can expand the MIME type allowlist.

Download failures (timeout, auth error, 404) produce a warning log and the file
is skipped — the text portion of the message still processes normally.

## Risks / Trade-offs

**[Risk] Session temp files lost on reboot** → Accepted for MVP. Media is
contextual, not archival. The agent's text reasoning persists. Future: could
move to a persistent blob store.

**[Risk] Large files bloat session directory** → Mitigated by existing
session cleanup policy. Could add per-file size limit (e.g., 20MB) and total
session media budget in future.

**[Risk] Slack file download timing** → Slack files may not be immediately
available after the event fires. Mitigated by retry with short backoff in the
download step.

**[Risk] Path traversal in attach_file** → Mitigated by validating the
canonical path starts with the session directory prefix. Reject anything
outside.

**[Risk] Protobuf schema evolution** → Additive-only change (new fields with
new tags). Existing journals deserialize correctly — new fields default to
empty. Forward-compatible.

## Actor Boundaries

- `LlmSessionActor` — owns the modality gate, processes `SendUserMessage`
  with media references, emits `FileOutput` via subscriber broadcast
- `SlackThreadBindingActor` — downloads Slack file attachments on inbound,
  uploads files on `FileOutput` outbound
- `ModelCapabilityActor` — unchanged, provides capability lookup used by
  the session actor for modality gate decisions

No new actors are introduced. The `attach_file` tool communicates with the
session actor via Tell (not Ask) to emit `FileOutput`.

## Failure Modes and Recovery

- **File missing at LLM call time**: `ChatMessageConverter` logs warning, skips
  media reference, sends text-only message. Session continues.
- **Slack download fails**: Warning logged, file skipped, text processes
  normally.
- **attach_file with invalid path**: Tool returns error text to the LLM
  ("File not found" or "Path outside session directory").
- **Session recovery from journal**: Media references in persisted events point
  to files that may no longer exist. Converter handles gracefully (skip missing
  files). The text conversation history is intact regardless.

## Open Questions

- Should `attach_file` support attaching multiple files in one call, or should
  the agent call it once per file?
- Should there be a configurable per-file size limit for inbound Slack
  attachments?
