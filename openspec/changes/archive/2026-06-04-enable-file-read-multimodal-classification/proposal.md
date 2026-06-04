## Why

`file_read` currently treats every file as UTF-8 text. When the agent points it
at images, PDFs, audio, video, archives, or other binary formats, the result can
be raw binary/gibberish in the tool response. That conflicts with Netclaw's
existing multimodal session pipeline: chat attachments already classify file
types, announce path-only files, and inline images only when the active model can
consume them.

The right fix is to make `file_read` follow the same file-type contract as chat
attachments without turning it into a document-processing engine.

## What Changes

- `file_read` detects file type before reading content.
- Text-like files continue to return text with the existing offset/limit and
  truncation behavior.
- Image files never return raw bytes. If the active model supports image input,
  `file_read` registers the image for the next LLM call through the existing
  session media path. If not, it returns metadata plus the canonical image
  modality-gap note.
- PDFs return metadata plus explicit guidance that native PDF extraction is not
  built into `file_read`.
- Audio/video, archives, Office-style binary documents, and unknown binary files
  return metadata plus explicit unsupported guidance.
- Chat attachment ingress and `file_read` share the same inline-decision helper
  so image/PDF/media behavior does not drift.

## Capabilities

### Modified Capabilities

- `netclaw-tools`: defines multimodal-aware `file_read` behavior and the
  side-channel that can add model-visible media after a tool result.
- `netclaw-input-adapters`: clarifies that the chat attachment file taxonomy and
  inline decision are also the canonical taxonomy for local file inspection.

## Impact

- **Source PRDs**: PRD-001, PRD-002, PRD-005, PRD-009.
- **Code**: `FileReadTool`, tool execution context/pipeline, session result
  handling, shared attachment inline decision helper.
- **Security**: existing `file_read` path authorization remains authoritative;
  this change does not apply chat ingress policy to local file reads.
- **Out of scope**: PDF extraction, OCR, Whisper/audio transcription, video
  keyframe extraction, and provider-specific non-image `DataContent` support.
