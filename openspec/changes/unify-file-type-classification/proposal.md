## Why

Netclaw now handles files across channel ingress, local tools, session media,
model input, content scanning, and provider serialization, but MIME decisions are
spread across string tables and prefix checks in multiple assemblies. This creates
drift between audience attachment policy and byte validation, makes it too easy
to trust declared transport metadata, and repeats security-sensitive code across
Slack, Discord, and Mattermost.

## What Changes

- Add a small `Netclaw.Media` library as the shared source of truth for MIME and
  media value objects, catalog metadata, extension mapping, category
  classification, model-input eligibility, and native signature definitions.
- Move MIME and attachment category primitives into strongly typed value objects
  from `Netclaw.Media` while preserving existing wire and disk formats.
- Replace broad MIME prefix classification with catalog-backed classification so
  only explicitly supported MIME types receive privileged categories such as
  `Image` or `Media`.
- Keep declared transport MIME separate from scanner-verified MIME. Channel
  ingress SHALL use declared MIME only for provisional pre-download policy gates
  and SHALL use verified canonical MIME after content scanning.
- Keep native magic-byte validation. Do not reintroduce `MimeDetective` or any
  runtime MIME detector package into the security path.
- Collapse duplicated attachment-ingress behavior across Slack, Discord, and
  Mattermost where practical, while leaving platform-specific URL trust and auth
  in each channel.
- Add provider-boundary guardrails so OpenAI-compatible serialization fails
  loudly if non-image `DataContent` reaches the image-only `image_url` path.

Out of scope:

- Native PDF text extraction, OCR, audio transcription, and video keyframe
  extraction.
- Generic provider-specific non-image `DataContent` support.
- Changing audience profile defaults or adding new attachment categories.
- Replacing `ChannelAttachmentPolicy`; security profiles remain the policy owner.

## Capabilities

### New Capabilities

- `netclaw-media`: Shared MIME/media value objects, catalog classification,
  extension mapping, model-input eligibility, and native signature metadata.

### Modified Capabilities

- `netclaw-input-adapters`: Channel attachment ingress distinguishes declared
  MIME metadata from scanner-verified MIME and uses catalog-backed categories for
  audience policy decisions.
- `netclaw-tools`: File-related tools and model-input handoff use typed MIME
  values and catalog-backed media support instead of raw strings and duplicated
  MIME maps.
- `value-object-integrity`: MIME and file-extension values crossing runtime,
  tool, actor, or persistence boundaries are represented by explicit value
  objects and do not implicitly decay to strings.

## Impact

- **Source PRDs**: PRD-001, PRD-002, PRD-005, PRD-006, PRD-009.
- **Code**: New `src/Netclaw.Media` project; updates to `Netclaw.Configuration`,
  `Netclaw.Security`, `Netclaw.Tools.Abstractions`, `Netclaw.Actors`,
  `Netclaw.Channels`, channel implementations, and OpenAI-compatible provider
  serialization.
- **Security**: Reduces trust in declared MIME strings, removes broad wildcard
  category classification, keeps byte validation native and fail-closed, and
  aligns scanner-supported MIME types with the media catalog.
- **Operations**: Existing `ChannelAttachmentPolicy` config shape and category
  names remain stable. Unsupported or unknown MIME types may be rejected earlier
  for Public/Team because category assignment becomes catalog-backed rather than
  prefix-backed.
- **Dependencies**: Adds an internal `Netclaw.Media` project. Adds no external
  MIME detection dependency.
