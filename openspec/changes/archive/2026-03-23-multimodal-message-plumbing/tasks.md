## 1. Persistence Types

- [ ] 1.1 Add `SerializableMediaReference` record to `SerializableChatMessage.cs` with `RelativePath`, `MimeType`, and `Modality` Protobuf fields
- [ ] 1.2 Add `List<SerializableMediaReference>` property to `SerializableChatMessage` at Protobuf tag 6
- [ ] 1.3 Add media references list to `SendUserMessage` at a new Protobuf tag alongside existing `Content` string

## 2. ChatMessageConverter

- [ ] 2.1 Update `ChatMessageConverter.ToAiMessage` to read media reference files from disk and produce `ImageContent` items in the `ChatMessage`
- [ ] 2.2 Handle missing files gracefully in `ToAiMessage` — log warning, skip media, preserve text
- [ ] 2.3 Update `ChatMessageConverter.FromAiMessage` to extract `ImageContent` and produce `SerializableMediaReference` entries
- [ ] 2.4 Unit test round-trip: `SerializableChatMessage` with media references → `ChatMessage` with `ImageContent` → back to serializable

## 3. Pipeline Plumbing

- [ ] 3.1 Update `ChannelPipeline.MapToCommand` to pass through all `AIContent` types — extract text to `Content` string AND convert non-text items to media references
- [ ] 3.2 Add `FileOutput` record to `SessionOutput.cs` with `FilePath`, `FileName`, and `MimeType` properties
- [ ] 3.3 Add `Files = 1 << 4` to `OutputFilter` flags and update `Full` preset to include it

## 4. Modality Gate

- [ ] 4.1 Add modality gate logic in `LlmSessionActor` `SendUserMessage` handler — compare content modalities against `SessionConfig.InputModalities`
- [ ] 4.2 Emit `TextOutput` acknowledgement when unsupported content is stripped
- [ ] 4.3 Skip LLM call when ALL content is unsupported (image-only message to text-only model)
- [ ] 4.4 Integration test: image sent to text-only model emits acknowledgement and processes text
- [ ] 4.5 Integration test: image sent to vision model passes through without acknowledgement

## 5. attach_file Tool

- [ ] 5.1 Implement `AttachFileTool` first-party tool — validates path within session directory, reads MIME type, emits `FileOutput` via session actor
- [ ] 5.2 Path traversal prevention: canonicalize path and verify it starts with session directory prefix
- [ ] 5.3 Register `attach_file` in `ToolRegistry` at startup
- [ ] 5.4 Unit test: valid path within session dir returns confirmation and emits FileOutput
- [ ] 5.5 Unit test: path traversal attempt returns error, no FileOutput emitted
- [ ] 5.6 Unit test: nonexistent file returns error

## 6. Slack Inbound (File Download)

- [ ] 6.1 Extend `SlackThreadBindingActor` (or its inbound processing) to detect `files` array in Slack message events
- [ ] 6.2 Download supported image MIME types (`image/png`, `image/jpeg`, `image/gif`, `image/webp`) using bot token authentication; call `IContentScanner.ScanAsync()` on downloaded bytes before writing to disk
- [ ] 6.3 Write downloaded files to session media directory (`{sessionDir}/media/{guid}.{ext}`)
- [ ] 6.4 Include media file references in `ChannelInput.Contents` as appropriate `AIContent` items
- [ ] 6.5 Skip unsupported MIME types with debug log
- [ ] 6.6 Handle download failures gracefully — warn log, skip file, continue with text

## 7. Slack Outbound (File Upload)

- [ ] 7.1 Handle `FileOutput` events in the Slack adapter's output rendering — call `files.uploadV2` with bot token
- [ ] 7.2 Handle upload failures gracefully — warn log, session continues

## 8. TUI Outbound

- [ ] 8.1 Handle `FileOutput` events in TUI adapter — print local file path to terminal

## 9. Testing

- [ ] 9.1 Integration test: full pipeline — `ChannelInput` with image → `SendUserMessage` with media ref → session state → `IChatClient` receives `ImageContent`
- [ ] 9.2 Integration test: `FakeChatClient` receives both `TextContent` and `ImageContent` in message list for vision model
- [ ] 9.3 Integration test: `attach_file` tool call emits `FileOutput` to subscriber
- [ ] 9.4 Serialization round-trip test: `SerializableChatMessage` with `MediaReferences` survives Protobuf serialize/deserialize

## 10. Existing Test Updates

- [ ] 10.1 Update existing integration tests that construct `SendUserMessage` to account for new media references field (additive, should be backward-compatible)

## 11. Content Security Project

- [x] 11.1 Create `Netclaw.Security` project with `Netclaw.Configuration` dependency and `Mime-Detective` package
- [x] 11.2 Add `IContentScanner` interface — `Task<ContentScanResult> ScanAsync(ReadOnlyMemory<byte>, string, string, CancellationToken)`
- [x] 11.3 Add `IPromptInjectionDetector` stub interface for future webhook scenarios
- [x] 11.4 Add `ContentScanResult` record with `IsAllowed`, `Error`, `DetectedMimeType`, `Message`
- [x] 11.5 Add `ContentScanError` enum — `UnrecognizedFileType`, `MimeTypeMismatch`, `ExecutableContent`, `EmptyContent`, `FileTooLarge`, `AntivirusDetection`, `ScanFailure`
- [x] 11.6 Add `PromptInjectionResult` record with `Risk` and `PromptInjectionRisk` enum
- [x] 11.7 Add `MimeType` value object — NO implicit string conversion, use `.Value`
- [x] 11.8 Add `MagicByteValidator` — magic byte detection using Mime-Detective, executable signature rejection, image-focused allowlist (png, jpeg, gif, webp)
- [x] 11.9 Add `FilenameSanitizer` — path traversal prevention, double-extension checks, control char stripping
- [x] 11.10 Add `ContentPolicy` — configurable allowed MIME types, max file size (default 20MB)
- [x] 11.11 Add `NullContentScanner` — no-op pass-through scanner as default
- [x] 11.12 Add `NullPromptInjectionDetector` — no-op pass-through detector
- [x] 11.13 Add `SecurityServiceExtensions.AddContentSecurity()` DI registration
- [x] 11.14 Add `IContentScanner` to `SlackGatewayDependencies` record
- [x] 11.15 Add `IContentScanner` to `SlackChannel` constructor and pass to dependencies
- [x] 11.16 Wire `AddContentSecurity()` in `Program.cs` DI
- [x] 11.17 Add projects to `Netclaw.slnx`, `Mime-Detective` to `Directory.Packages.props`
- [x] 11.18 Add `Netclaw.Security` project reference to `Netclaw.Channels.csproj`

## 12. Content Security Tests

- [x] 12.1 Create `Netclaw.Security.Tests` project
- [x] 12.2 `MagicByteValidatorTests` — PNG/JPEG/GIF/WebP pass with valid magic bytes
- [x] 12.3 `MagicByteValidatorTests` — executable bytes rejected (EXE, ELF, shebang)
- [x] 12.4 `MagicByteValidatorTests` — MIME type mismatch rejected, unknown extension rejected, file too large rejected
- [x] 12.5 `FilenameSanitizerTests` — path traversal variants stripped
- [x] 12.6 `FilenameSanitizerTests` — double-extension detection, control chars, long names
- [x] 12.7 `NullScannerTests` — NullContentScanner and NullPromptInjectionDetector pass-through
