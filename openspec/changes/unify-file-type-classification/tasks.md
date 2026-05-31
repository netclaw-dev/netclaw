## 1. Media Foundations

- [x] 1.1 Add `Netclaw.Media` project and wire it into the solution/build.
- [x] 1.2 Add MIME, declared MIME, verified MIME, file-extension, media kind,
  and attachment category value types with no implicit primitive conversions.
- [x] 1.3 Build the catalog with canonical MIME values, aliases, extensions,
  categories, text/binary classification, scanner support, default extensions,
  and model-input eligibility.
- [x] 1.4 Add catalog tests for aliases, unknown prefix handling, categories,
  extensions, text/binary classification, and model-input eligibility.

## 2. Configuration and Security Integration

- [x] 2.1 Move `AttachmentCategory` usage in configuration and schema-facing code
  to `Netclaw.Media` while preserving JSON enum values.
- [x] 2.2 Refactor `MagicByteValidator` and `ContentPolicy` to use catalog-backed
  supported MIME definitions and native signature matchers.
- [x] 2.3 Update `ContentScanResult` to expose verified canonical MIME for
  accepted files and maintain detected MIME on rejected files.
- [x] 2.4 Add security tests for verified MIME, octet-stream PNG acceptance,
  spoofed executable rejection, and catalog/scanner allowlist alignment.

## 3. Tool and Actor MIME Typing

- [x] 3.1 Update `ToolExecutionContext`, `FileAttachmentInfo`, and
  `ModelInputFileInfo` to carry typed canonical MIME values.
- [x] 3.2 Update `SerializableMediaReference` protobuf mapping to keep MIME on
  the wire as a primitive string while using the media value object in memory.
- [x] 3.3 Replace `MediaMimeClassifier`, `AttachmentCategories.FromMime`, and
  direct MIME prefix checks with catalog lookups.
- [x] 3.4 Update `file_read`, `attach_file`, model-input materialization, and
  `web_fetch` to use catalog-backed MIME decisions.

## 4. Channel Ingress Consolidation

- [x] 4.1 Update Slack, Discord, and Mattermost attachment refs to preserve
  declared MIME as typed metadata.
- [x] 4.2 Use catalog classification for provisional pre-download audience gates.
- [x] 4.3 Use scanner-verified MIME for accepted attachment lines, inlined
  `DataContent`, session media references, and accepted logs.
- [x] 4.4 Extract shared attachment-ingress mechanics into `Netclaw.Channels`
  where it reduces duplication without hiding platform URL/auth differences.
- [x] 4.5 Add channel tests for octet-stream PNG acceptance, unknown media subtype
  rejection for Team, executable spoof rejection, and verified MIME in output.

## 5. Provider Guardrails and Documentation

- [x] 5.1 Add fail-loud OpenAI-compatible provider guardrail for non-image
  `DataContent` on the `image_url` serialization path.
- [x] 5.2 Update `netclaw-operations` system skill and bump its metadata version.
- [x] 5.3 Validate OpenSpec artifacts for this change.
- [x] 5.4 Run targeted test projects for media, security, configuration, actors,
  daemon/provider behavior, then required quality gates.
