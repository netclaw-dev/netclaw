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
  downscales an image to a bounded payload: long-edge cap (~1568px, Anthropic's
  documented sweet spot) **and** a base64 byte budget (~5MB, matching
  Anthropic's hard API limit), re-encoding to JPEG for photos while preserving
  PNG where it matters.
- Use **shrink-on-decode** (`SKCodec` sample-size) so the full-resolution
  bitmap is never materialized — the memory ceiling is the *downscaled*
  bitmap, not the source. This is the actual #1296 fix, not just a smaller
  output.
- Two seams feed the one normalizer:
  - **Chat attachments → normalize at ingestion** (the OpenCode model): when
    media is admitted to the session store, store the already-shrunk artifact.
    The egress converter then reads a small file and barely changes.
  - **`file_read` images → downscale at egress + content-hash cache** (the
    Codex model): these have no ingestion seam, so shrink on first send and
    cache the encoded result keyed by content hash, making turns 2..N free.
- **Fail loud, never silently passthrough.** An image that cannot be shrunk
  under budget (or is corrupt / undecodable) is **dropped with a visible
  `[image omitted: …]` note**, never shipped raw. No silent fallback.
- Add a configurable image-egress block (long-edge cap, byte budget, JPEG
  quality, enable/disable) to `Netclaw.Configuration`, with the matching
  `netclaw-config.v1.schema.json` update in the same PR (schema-sync rule).
- Add **SkiaSharp** (MIT; `SkiaSharp.NativeAssets.Linux.NoDependencies` for the
  Debian-bookworm container, native assets for win/macOS dev daemons) as a
  dependency. Rejected ImageSharp (Six Labors Split License — commercial
  trap), NetVips (LGPL native), Magick.NET (Apache match but heaviest memory),
  MagicScaler (asymmetric cross-platform setup); see design.md.

### In scope (MVP)
- Image downscale + re-encode at both seams, memory-bounded decode, content-hash
  cache for the `file_read` path, fail-loud drop, configurable caps.

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
  normalizer; the encoded result SHALL be produced at most once per distinct
  image (ingestion-normalized for chat media, content-hash cached for
  `file_read`); un-shrinkable or undecodable images SHALL be dropped with a
  visible note rather than passed through; caps SHALL be configurable.

### Modified Capabilities
- `netclaw-tools`: The `file_read` model-input image handoff (the
  `AddModelInputFile` path under the "Model-input media eligibility" and "File
  read tool" requirements) SHALL route the image through the bounded normalizer
  + cache instead of handing the raw on-disk file to egress.

## Impact

- **Code:** new `ImageNormalizer` + pure `ChooseDecodeSampleSize` helper (home:
  `Netclaw.Media`); `ChatMessageConverter.ToAiMessage` (egress read);
  `SessionMediaStore.WriteDataContent` / channel admission (ingestion
  normalize); `FileReadTool` `AddModelInputFile` path (egress normalize +
  cache); new `*Config` in `Netclaw.Configuration` + schema JSON.
- **Dependencies:** add SkiaSharp + Linux native assets; wire native assets
  into `docker/Dockerfile` (Debian bookworm-slim, glibc — no musl concern).
- **Persistence/serialization:** if `MediaReference` gains a normalized marker,
  it stays framework-owned and round-trip safe.
- **Security/operational:** reduces peak heap per image from O(full-res bitmap +
  full base64) to O(downscaled bitmap + bounded base64), and from per-turn to
  once — closing the #1296 OOM vector. Fail-loud drop keeps a misconfiguration
  from silently shipping huge payloads. Operational: new config knobs documented
  in CLI help / runbook; `netclaw doctor --fix` defaults provided for clean
  upgrades.
- **Quality gates:** eval suite (image tool cases) as the only end-to-end
  backstop; the rest is deterministic unit tests (normalizer transforms, decode
  sample-size math, memory ceiling, cache invocation counts, fail-loud drop,
  config/schema). Termina smoke harness does **not** apply (no TUI surface).
