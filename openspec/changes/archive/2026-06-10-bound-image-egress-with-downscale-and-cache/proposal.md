## Why

Today every image in the un-compacted window is read off disk in full and
base64-inlined into the provider request on **every** turn — no downscale, no
cap beyond admission (`ChatMessageConverter.cs` does `File.ReadAllBytes` +
`DataContent` over the whole history each turn; see issue #1296). A 25 MB photo
decodes to ~200 MB of bitmap and re-materializes per turn; 10×25 MB of admitted
media is ~660 MB of base64 in a single request. On the memory-limited daemon
(we run at 1Gi) this is a real OOM vector and the same allocate-then-cap shape
#1293/#1300/#1301 already removed from the text paths — just for media.

It buys nothing: every major provider downscales server-side anyway (Anthropic
caps at 1568px / ~1.15MP, OpenAI at 2048→768 short side, Gemini tiles at 768),
so the full-resolution bytes carry no additional model signal. The two
best-in-class OSS coding harnesses (OpenCode, OpenAI Codex CLI) downscale once
and reuse; we are currently in the worst tier (Aider/gptme: re-read disk +
re-encode every turn). This change brings our image egress in line with what
those harnesses ship.

Governing PRDs: PRD-001 (MVP resource posture / single-process daemon),
PRD-002 (Gateway Security Envelope — memory-safety and fail-loud on the
"return external content to the model" path), PRD-005 (Model Provider Strategy —
multimodal model input).

## What Changes

- Introduce a shared, injectable **`ImageNormalizer`** (SkiaSharp-backed) that
  **only resizes**: an image whose longest edge exceeds the cap (~1568px,
  Anthropic's documented sweet spot) is downscaled and re-encoded **in its
  original container format** — a PNG stays a PNG, a JPEG stays a JPEG. No
  transcoding (e.g. PNG→JPEG), no quality reduction. A base64 byte budget (~5MB,
  Anthropic's hard API limit) acts as a fail-loud drop gate: anything that can't
  be bounded by resize alone (or a format Skia can't re-encode, e.g. GIF, that is
  over budget) is dropped, never silently degraded.
- Use **shrink-on-decode** (`SKCodec` sample-size) so the full-resolution
  bitmap is never materialized — the memory ceiling is the *downscaled*
  bitmap, not the source. This is the actual #1296 fix, not just a smaller
  output.
- **One seam: the session media-store write boundary.** Every image — chat
  attachments (`WriteDataContent`) and `file_read` model-input handoffs
  (`CopyFile`) — is written to session media exactly once and read back from
  there on every turn (including turn 1). Normalizing at that boundary bounds
  every image once at ingestion; the egress read path is unchanged and there is
  **no separate per-turn cache** (the persisted artifact *is* the dedup). Earlier
  drafts proposed a `file_read` content-hash cache on the false premise that
  `file_read` images are re-read from disk each turn — they are not; `CopyFile`
  persists them into media on first use, exactly like chat attachments.
- **Fail loud, never silently passthrough.** An image that cannot be shrunk
  under budget (or is corrupt / undecodable) is **dropped with a visible
  `[image omitted: …]` note**, never shipped raw. No silent fallback.
- **Fixed safe constants, not configuration.** The bounds default to ~1568px /
  ~5MB. They are **not** user-raisable: the byte budget is the memory-safety
  lever, so a config knob could re-open the very OOM this fixes. No
  `netclaw-config.v1.schema.json` change.
- Add **SkiaSharp** (MIT; `SkiaSharp.NativeAssets.Linux.NoDependencies` for the
  Debian-bookworm container, native assets for win/macOS dev daemons) as a
  dependency. Rejected ImageSharp (Six Labors Split License — commercial
  trap), NetVips (LGPL native), Magick.NET (Apache match but heaviest memory),
  MagicScaler (asymmetric cross-platform setup); see design.md.

### In scope (MVP)
- Image downscale + re-encode at the single media-store seam, memory-bounded
  decode, fail-loud drop. Fixed safe constant bounds.

### Out of scope
- **Provider Files API / `file_id` upload-by-reference.** Zero of nine surveyed
  OSS coding harnesses use it for image input, and our wrapped community
  Anthropic SDK cannot reach it. Deferred; revisit only if we own the provider
  plugin. The normalizer leaves a clean seam for it.
- **Audio/video egress (#1266, #1297).** Separate issues. This change must
  **not** extend the inline-base64 path to A/V; it only leaves them a seam.
- **Streaming the base64 encode.** Largely moot once payloads are bounded.

## Capabilities

### New Capabilities
- `bounded-image-egress`: Image bytes handed to a model SHALL be downscaled to a
  bounded payload (long-edge + byte budget) via a memory-bounded shrink-on-decode
  normalizer; normalization SHALL happen once at the session media-store write
  boundary so every later turn reads the already-bounded artifact (no per-turn
  cache); un-shrinkable or undecodable images SHALL be dropped with a visible note
  rather than passed through; the bounds SHALL be fixed memory-safe constants, not
  raisable by configuration.

### Modified Capabilities
- `netclaw-tools`: The `file_read` model-input image handoff (the
  `AddModelInputFile` path under the "Model-input media eligibility" requirement)
  SHALL normalize the image as it is copied into session media (`CopyFile`)
  instead of handing the raw on-disk file to egress.

## Impact

- **Code:** new `IImageNormalizer` + pure `ImageDecodeMath` (home:
  `Netclaw.Media`, done); normalization hooked into the two `SessionMediaStore`
  writers — `WriteDataContent` (chat attachments) and `CopyFile` (`file_read`
  model-input handoff). `ChatMessageConverter` egress read is unchanged (it
  already reads the persisted artifact). No new `*Config`, no schema change.
- **Dependencies:** add SkiaSharp + Linux native assets; wire native assets
  into `docker/Dockerfile` (Debian bookworm-slim, glibc — no musl concern).
- **Persistence/serialization:** the normalized artifact replaces the original at
  the media-store write; stored length/MIME reflect the bounded artifact. No
  `MediaReference` shape change required.
- **Security/operational:** reduces peak heap per image from O(full-res bitmap +
  full base64) to O(downscaled bitmap + bounded base64), and from per-turn to
  once — closing the #1296 OOM vector. Fail-loud drop keeps a corrupt/oversized
  image from silently shipping huge payloads. Bounds are fixed constants, so no
  configuration can re-open the OOM.
- **Quality gates:** eval suite (image tool cases) as the only end-to-end
  backstop; the rest is deterministic unit tests (normalizer transforms, decode
  sample-size math, memory ceiling, fail-loud drop, media-store seam round-trip).
  Termina smoke harness does **not** apply (no TUI surface).
