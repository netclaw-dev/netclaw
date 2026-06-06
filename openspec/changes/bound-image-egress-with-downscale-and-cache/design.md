## Context

`ChatMessageConverter.ToAiMessage` reads each `MediaReference` with
`File.ReadAllBytes` and wraps it as MEAI `DataContent` (`ChatMessageConverter.cs:99-100`).
That converter runs over the **entire history** on every turn — request assembly
(`SessionMessageAssembler.cs:112`) and compaction (`SessionCompactionPipeline.cs:64`) —
so an admitted image is re-read off disk, re-buffered, and re-base64-encoded on
each turn it remains in the un-compacted window. There is no image-processing
library anywhere in the egress path today, so there is zero downscaling: a 25 MB
source decodes to ~200 MB of bitmap and inflates ~33% as base64. Admission caps
(`ChannelAttachmentPolicy`, 25 MB/file × 10 files) bound what enters, not the
per-turn materialization. On the 1Gi daemon this is the #1296 OOM vector.

Images reach the model through two distinct entry points, which is the central
design constraint:

1. **Chat attachments** — admitted by a channel, written to the session media
   store (`SessionMediaStore.WriteDataContent`), persisted as a `MediaReference`,
   rehydrated at egress. There **is** an ingestion seam.
2. **`file_read` model-input handoff** — `FileReadTool` calls
   `context.AddModelInputFile(path, ...)` for an arbitrary on-disk image the
   agent points at. There is **no** ingestion seam; the file just exists on disk.

Provider research (Anthropic ≤1568px/1.15MP, OpenAI 2048→768, Gemini 768 tiles)
confirms full-resolution bytes carry no additional model signal — every provider
downscales server-side. A survey of nine OSS coding harnesses found the two
best-in-class (OpenCode, OpenAI Codex CLI) downscale once and reuse; the worst
(Aider, gptme) match our current behavior exactly.

## Goals / Non-Goals

**Goals:**
- Bound peak per-image memory to the *downscaled* target, not the source
  resolution, and make the encode happen **once** per distinct image rather than
  per turn. (Closes #1296.)
- One shared normalizer behind both entry points, so there is a single place
  that owns "how an image is prepared for a model."
- Fail loud: never silently ship unbounded bytes.
- Configurable caps with schema sync and clean upgrades.
- Keep the change deterministically testable without a live LLM call.

**Non-Goals:**
- Provider Files API / `file_id` upload-by-reference (no surveyed harness uses it
  for images; our wrapped Anthropic SDK can't reach it). Leave a seam only.
- Audio/video egress (#1266, #1297) — must **not** be folded into the inline
  path; only leave them a seam.
- Streaming the base64 encode (moot once payloads are bounded).
- Changing admission caps or the audience/ACL gates.

## Decisions

### D1: SkiaSharp as the image library

Chosen over the alternatives primarily on **license + cross-platform
uniformity**, with shrink-on-decode as the gating capability:

- **ImageSharp — rejected.** Six Labors Split License requires a paid commercial
  license above a revenue threshold; a real trap for Petabridge regardless of
  the repo being Apache-2.0. (Its only advantage — pure-managed, no native dep —
  is moot for us since we ship a container we control.)
- **NetVips (libvips) — rejected.** Best memory/perf and matches Continue's
  `sharp`, but native libvips is **LGPL-2.1+**, adding dynamic-link compliance
  obligations.
- **Magick.NET — rejected.** Apache-2.0 (exact repo match) but the heaviest
  memory profile (full-image decode) — ironic for an OOM fix.
- **PhotoSauce MagicScaler — rejected.** MIT and lowest-memory, but
  **asymmetric** cross-platform setup: Windows uses built-in WIC while
  Linux/macOS need bolt-on `NativeCodecs.*` plugins. Its weak platform is Linux
  (our primary), inviting "works on Windows, differs in the pod" drift on a
  security-sensitive path.
- **SkiaSharp — chosen.** MIT wrapper, BSD-3 native (Skia). `NativeAssets.{Win32,
  macOS,Linux.NoDependencies}` give one identical code path on the Debian
  bookworm-slim (glibc) container and on Windows/macOS dev daemons, with no
  per-format plugin wiring. `SKCodec` with `SKCodecOptions` sample-size provides
  shrink-on-decode, so we avoid the full-res bitmap. Use the `NoDependencies`
  Linux asset (we do no text rendering, so fontconfig is not needed).

### D2: Hybrid seam — normalize-at-ingestion for chat media, downscale-at-egress + cache for file_read

Two entry points, two correct placements, **one** normalizer:

- **Chat attachments (ingestion):** normalize in/around
  `SessionMediaStore.WriteDataContent` so the persisted artifact is already
  bounded. The egress converter then reads a small file — `ChatMessageConverter`
  barely changes. This is the OpenCode model and fixes the common case at the
  source.
- **`file_read` (egress):** there is no ingestion point, so normalize when the
  model-input file is materialized and cache the encoded result keyed by content
  hash. This is the Codex model and makes turns 2..N free.

Alternative considered: normalize *only* at egress for both. Rejected — it
leaves the persisted chat artifact unbounded (every recovery/compaction re-reads
the big file) and needs the cache to carry the common case it could have avoided
entirely.

### D3: Bound by long-edge AND byte budget

Cap the long edge (~1568px, Anthropic's documented sweet spot) **and**
shrink-to-fit an encoded base64 byte budget (~5MB, Anthropic's hard limit). The
byte budget is the quantity #1296 actually cares about (request size / heap); a
pixel cap alone is a proxy. This mirrors OpenCode's iterative shrink. Bound the
iteration (fixed step count) so it always terminates.

### D4: Normalizer is a pure, injectable component returning a rich result

`IImageNormalizer` takes bytes (or an opened `SKCodec`) + caps and returns a
result record: `{ outcome (Normalized | PassedThrough | Dropped), bytes?,
width, height, encodedByteLength, mediaType, reason? }`. It **throws nothing** on
bad input — undecodable/un-shrinkable inputs return `Dropped` with a reason. The
decode sample-size decision is a separate pure function
`ChooseDecodeSampleSize(srcW, srcH, longEdgeCap) -> int`. This shape is what
makes the behavior unit-testable without an LLM and keeps the fail-loud contract
explicit at the type level.

Home: `Netclaw.Media` (already the media library; SkiaSharp is an external dep,
so this does not violate its "no dependency on other Netclaw projects" rule).

### D5: Format policy

Re-encode photos to JPEG (configurable quality, default ~85); preserve PNG for
images with alpha or where lossless matters (screenshots/diagrams), matching the
Codex passthrough-when-supported approach. When passing through an
already-bounded supported format, avoid a needless re-encode.

### D6: Fail-loud drop

On `Dropped`, the converter/handoff emits a `[image omitted: <reason>]` text
part and attaches no image content. No silent fallback to raw bytes (constitution
rule). For chat media that fails at ingestion, surface the drop at admission time
where possible.

### D7: Testing strategy (no end-to-end LLM)

The LLM sits at the end of the pipe; everything before is deterministic
bytes-in/bytes-out. Coverage:

- **Normalizer transforms** — generate fixtures in-test with SkiaSharp (draw an
  N×M canvas, encode), assert output dims/bytes for oversized-by-dimension,
  oversized-by-bytes (terminates), already-small (no upscale), format rules,
  corrupt/un-shrinkable → `Dropped`.
- **Memory ceiling** — unit-test `ChooseDecodeSampleSize` (pure integer math);
  one integration test asserts the decoded buffer for an 8000×8000 fixture is
  ~target-sized, plus a coarse `GC.GetAllocatedBytesForCurrentThread()` ceiling
  ("stays under N MB", assert a bound not an exact value).
- **Cache** — inject a spy normalizer, assert same-bytes → one invocation,
  different-bytes → two, LRU eviction over capacity. Inject `TimeProvider` for
  any TTL (no `Task.Delay`).
- **Seams** — ingestion: write oversized image, read stored artifact back,
  assert bounded. Egress: extend `ChatMessageConverterTests` to assert
  `DataContent.Data.Length` bounded + media type; drop-with-note path.
- **Config/schema** — config-load test with and without the block (defaults +
  `additionalProperties:false` acceptance).
- **End-to-end backstop** — 1–2 eval-suite image cases only. Termina smoke
  harness does **not** apply (no TUI surface).

## Risks / Trade-offs

- **SkiaSharp native asset in the container** → wire
  `SkiaSharp.NativeAssets.Linux.NoDependencies` into `docker/Dockerfile` and
  verify on the glibc bookworm-slim base; add a startup/`doctor` probe that the
  native lib loads so a packaging regression fails loud rather than at first
  image.
- **A pathological decode still spikes before sample-size applies** → use
  `SKCodec`'s scaled-dimensions API to pick the sample-size from header
  dimensions *before* allocating the pixel buffer; the integration memory test
  guards this.
- **Lossy re-encode degrades a text-heavy screenshot** → keep PNG for
  lossless/alpha cases (D5); 1568px long edge keeps text legible (Anthropic's
  own recommendation); quality configurable.
- **Cache unbounded growth** → bound the `file_read` cache (LRU by count and/or
  total bytes); it holds already-small encoded artifacts.
- **Behavior drift between ingestion and egress paths** → both call the *same*
  `IImageNormalizer`; the only difference is *where* and *whether the result is
  persisted vs cached*. No second implementation.
- **Persistence/recovery** → if `MediaReference` gains a "normalized" marker it
  stays framework-owned and serialization round-trip safe; recovery rehydrates
  the already-bounded artifact. The `file_read` cache is in-process and
  rebuildable, so it needs no persistence and is safe to lose on restart (it
  re-normalizes on next reference — correct, just not free).

## Migration Plan

1. Add SkiaSharp + Linux native asset; wire into Dockerfile; add load probe.
2. Land `IImageNormalizer` + `ChooseDecodeSampleSize` in `Netclaw.Media` with
   unit tests (no wiring yet).
3. Wire the egress `file_read` path + content-hash cache.
4. Wire the ingestion (chat-media) path; persist bounded artifact.
5. Add `*Config` + `netclaw-config.v1.schema.json` sync (defaults for clean
   upgrade).
6. Eval cases + benchmark before/after memory number for the PR.

Rollback: the config enable/disable switch (D... config) turns normalization off,
reverting to passthrough behavior without code changes; the native-asset add is
additive.

## Open Questions

- Exact default caps — confirm 1568px / 5MB / JPEG q85, or expose only and ship
  conservative defaults.
- Whether to keep the original alongside the normalized chat artifact (cost: 2×
  disk) or store only the normalized one (OpenCode does the latter). Leaning
  normalized-only since the model never needs the original.
- Cache key/eviction bounds (count vs total-bytes) and default size.
