## Context

Netclaw currently handles files in several paths: inbound channel attachments,
historical thread backfill, `file_read`, `attach_file`, session media
persistence, model-input handoff, web fetch output, and OpenAI-compatible
provider serialization. These paths all need MIME facts, but the facts are
spread across `MimeTypeCatalog`, `AttachmentCategories`, `MediaMimeClassifier`,
`MagicByteValidator`, tool records, and ad hoc provider/channel checks.

The highest-risk drift is between security policy and content validation. The
audience profiles allow coarse attachment categories, while the scanner validates
specific MIME/extension/signature combinations. Earlier drift between those
layers rejected PDFs and documents after policy accepted them. Earlier reliance
on `MimeDetective` also caused process-lifetime attachment failures when static
type initialization failed. The new design keeps the native validator but makes
the catalog explicit and shared.

## Goals / Non-Goals

**Goals:**

- Introduce `Netclaw.Media` as the low-dependency home for MIME/media value
  objects and catalog facts.
- Replace raw MIME strings at internal boundaries with typed values where the
  type carries meaning: declared metadata, verified MIME, extension, and
  canonical MIME.
- Make attachment category assignment catalog-backed instead of broad prefix-
  backed.
- Make scanner-accepted MIME the downstream MIME used for attachment lines,
  `DataContent`, media refs, and model-input checks.
- Keep existing persisted and wire formats stable.
- Reduce duplicated attachment-ingress code without merging channel-specific URL
  trust or auth decisions.

**Non-Goals:**

- Reintroducing `MimeDetective` or another external MIME detector.
- Native extraction for PDFs, Office documents, archives, audio, or video.
- Generic non-image `DataContent` provider support.
- Changing the `ChannelAttachmentPolicy` config shape or audience defaults.
- Moving tool authorization, ACL logic, or channel trust decisions into
  `Netclaw.Media`.

## Decisions

### D1. Add `Netclaw.Media` with no Netclaw project references

`Netclaw.Media` will contain value objects, enums, catalog definitions, and
native signature metadata. It will not depend on `Netclaw.Configuration`,
`Netclaw.Security`, actors, channels, or providers. This lets configuration,
security, tools, actors, channels, and providers all depend on the same media
types without creating dependency cycles.

Alternative considered: put the types in `Netclaw.Configuration`. That avoids a
new project, but turns configuration into a dumping ground for byte signatures
and model-input media facts. The new project gives the domain a clear boundary.

### D2. Keep security policy separate from media classification

`Netclaw.Media` classifies what a file claims to be and what the catalog knows
about it. `ChannelAttachmentPolicy` still decides whether an audience may accept
that category. `IContentScanner` and `ContentPolicy` still decide whether bytes
are valid and allowed.

This preserves the existing profile model:

- Public: `Image`
- Team: `Image`, `Pdf`, `Document`, `Archive`, `Media`
- Personal: `Image`, `Pdf`, `Document`, `Archive`, `Media`, `Other`

### D3. Treat declared MIME and verified MIME as different values

Channel transports provide declared MIME metadata. It is useful for early
rejection and logs, but it is not proof. The scanner returns verified canonical
MIME when bytes and filename validate. Downstream code uses verified MIME where
available.

This means a declared `application/octet-stream` PNG can be accepted if the
filename and bytes validate as PNG, but a declared `image/png` executable is
rejected before it reaches session state or the LLM.

### D4. Native byte validation remains explicit

`MagicByteValidator` keeps native signature matchers for supported formats. The
catalog can describe which MIME types have validation support, but the security
assembly owns scanner policy and result construction. This avoids a runtime
dependency that can fail during type loading while preserving explicit hardened
checks for executable prefixes and malformed magic headers.

### D5. Consolidate repeated ingress mechanics after typing is stable

Slack, Discord, and Mattermost currently repeat the same policy, size, download,
scan, inbox, inline, and formatting flow. After MIME types and scanner results
are typed, common mechanics can move into a shared channel helper. Platform code
keeps URL trust validation and download auth callbacks.

## Risks / Trade-offs

- Catalog mistakes become central mistakes -> add catalog tests covering aliases,
  categories, default extensions, scanner support, and model-input eligibility.
- Moving `AttachmentCategory` changes namespaces across config code -> keep enum
  names and JSON schema values unchanged.
- Wrapping MIME values can create noisy `.Value` callsites -> prefer typed helper
  APIs on the catalog so callers rarely need string access.
- Large channel-ingress consolidation could obscure platform-specific behavior ->
  extract shared helpers only after tests pin each channel's trust/download
  differences.

## Migration Plan

1. Add `Netclaw.Media` and tests without changing behavior.
2. Move `MimeType` and `AttachmentCategory`; preserve protobuf and JSON string
   formats.
3. Route existing classifiers through the catalog.
4. Refactor scanner results to carry verified MIME and update channel flows to
   use it after scan.
5. Consolidate common channel-ingress mechanics only once behavior is covered by
   regression tests.

Rollback is straightforward before persistence schema changes because the wire
and disk shapes remain strings and existing category names remain unchanged.

## Open Questions

- Whether to add a dedicated `Netclaw.Media.Tests` project or keep catalog tests
  in `Netclaw.Security.Tests` and `Netclaw.Configuration.Tests`. A dedicated test
  project is preferred if the catalog grows beyond simple mapping tests.
