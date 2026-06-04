## 1. OpenSpec and shared behavior

- [x] 1.1 Add change artifacts for `enable-file-read-multimodal-classification`.
- [x] 1.2 Add delta specs for `netclaw-tools` and `netclaw-input-adapters`.
- [x] 1.3 Extract or add a shared inline-decision helper used by chat attachments
  and `file_read`.

## 2. File-read implementation

- [x] 2.1 Add MIME/category inspection to `file_read` without breaking existing
  text/code reads, including common non-UTF-8 encodings.
- [x] 2.2 Return metadata/guidance instead of raw binary for PDF, audio/video,
  archive, binary document, and unknown binary files.
- [x] 2.3 Add image handoff from `file_read` through `ToolExecutionContext`, the
  tool execution pipeline, and `LlmSessionActor` into session media refs.

## 3. Tests, docs, and verification

- [x] 3.1 Add or update tests for text reads, binary guidance, image handoff, and
  image unsupported-model behavior, including streaming and sub-agent handoff.
- [x] 3.2 Update the Netclaw operations system skill guidance for inbound
  attachments and `file_read` behavior.
- [x] 3.3 Run targeted tests for tools/session media behavior.
- [x] 3.4 Run required quality gates: `dotnet slopwatch analyze` and
  `./scripts/Add-FileHeaders.ps1 -Verify`.
