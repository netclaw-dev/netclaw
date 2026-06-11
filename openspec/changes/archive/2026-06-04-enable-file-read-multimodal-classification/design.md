## Context

Netclaw already has a file-backed multimodal path for inbound channel content:
channel adapters emit `DataContent`, `ChannelPipeline` writes it into the session
`media/` directory, `SerializableMediaReference` persists a safe reference, and
`ChatMessageConverter` rehydrates that reference into `DataContent` at the LLM
boundary. That path is currently used for images only. PDFs are intentionally
path-only because OpenAI-compatible providers serialize all `DataContent` as
`image_url`, which previously broke `application/pdf` payloads.

`file_read` sits in a different path. It is a first-party tool whose execution
contract returns text. The tool pipeline wraps that text in `FunctionResultContent`.
Trying to stuff binary data or `DataContent` into a tool result would fight both
the current tool abstraction and provider tool-result semantics.

## Goals / Non-Goals

**Goals:**

- Stop `file_read` from returning raw binary/gibberish for non-text files.
- Keep normal text/code-file reads working exactly as they do today.
- Let `file_read` hand images to vision-capable models through the existing
  session media pipeline.
- Keep behavior aligned with chat attachment classification and inline decisions.
- Return explicit, useful metadata/guidance for unsupported formats.

**Non-Goals:**

- No native PDF text extraction inside `file_read`.
- No OCR inside `file_read`.
- No audio transcription or video keyframe extraction.
- No provider-specific non-image media serialization.
- No reuse of `ChannelAttachmentPolicy` as a local file-read policy gate.

## Decisions

### D1. `file_read` remains a text-result tool

The tool result remains a string. Non-text files return metadata and guidance;
they do not return bytes. This keeps provider tool-call ordering valid and avoids
binary payloads in `FunctionResultContent`.

### D2. Model-visible images use a side channel

When `file_read` reads an image and the active model supports image input, the
tool registers a model-input file on `ToolExecutionContext`. The session and
sub-agent tool pipelines copy that file into the session `media/` directory and
return `SerializableMediaReference` records alongside the normal text tool
result. Main-session streaming tool results persist those refs on the tool-result
message so journal replay can recreate the media nudge. After all sibling tool
results are present, the session or sub-agent loop appends a system nudge carrying
those media references so the next LLM call sees the image through the existing
`ChatMessageConverter` path.

### D3. Same taxonomy, separate policy

`file_read` uses the same file categories as chat attachments:
`Image`, `Pdf`, `Document`, `Archive`, `Media`, and `Other`. It does not use
`ChannelAttachmentPolicy` because that policy governs untrusted chat ingress
before download. Local file reads already run through `AllowedTools`, `ReadFiles`,
`GlobalReadRoots`, `ToolPathPolicy`, and audience-scoped filesystem policy.

### D4. Explicit unsupported responses

Unsupported formats return structured metadata with next-step guidance. A PDF
response says PDF extraction is not built into `file_read`. Audio/video responses
say transcription or keyframe extraction requires a configured processor. Archives
and unknown binaries say the bytes are not readable as text.

## Risks / Trade-offs

- A tool-result side channel adds one more shape to `ToolExecutionContext`, but it
  mirrors the existing `FileAttachmentInfo` side channel and keeps tool results
  string-only.
- Text detection must not break code-file reads with unfamiliar extensions or
  common user-authored text encodings. The implementation should treat valid
  UTF-8 without binary control bytes as text even when the MIME type is unknown,
  and should recognize UTF-16/UTF-32 Unicode plus Windows-1252 when the extension
  is text-like.
- Image files are copied into `media/`; this duplicates bytes for files already
  under `inbox/`, but keeps the LLM boundary simple and path-local.
