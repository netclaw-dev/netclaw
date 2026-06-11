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
- Fixed memory-safe bounds (constants) that no configuration can weaken.
- Keep the change deterministically testable without a live LLM call.

**Non-Goals:**
- Provider Files API / `file_id` upload-by-reference (no surveyed harness uses it
  for images; our wrapped Anthropic SDK can't reach it). Leave a seam only.
- Audio/video egress (#1266, #1297) — must **not** be folded into the inline
  path; only leave them a seam.
- Streaming the base64 encode (moot once payloads are bounded).
- A per-turn / content-hash encode cache (the change name's "-and-cache" is now
  vestigial). The session media store already persists the normalized artifact
  once; a cache would buy nothing for the OOM — see D2.
- Runtime configuration of the bounds (would re-open the OOM — see D... bounds).
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

### D2: One seam — normalize at the `SessionMediaStore` write boundary (no cache)

`SessionMediaStore` has exactly two write methods, and they are the only way bytes
enter media:

- `WriteDataContent` — chat attachments (`ChannelPipeline`) and persisted message
  media (`ChatMessageConverter.FromAiMessage`).
- `CopyFile` — `file_read` / model-input handoff
  (`SessionToolExecutionPipeline.MaterializeModelInputFiles`).

Both origins are read back on every turn (including turn 1) by the single egress
read in `ChatMessageConverter.ToAiMessage` → `GetMediaPath` + `ReadAllBytes`.
Normalize inside the two writers (gated to images; non-image media passes
through), and **every image from either origin is bounded exactly once, at the
moment it is persisted.** The egress read path needs no change, and there is **no
separate per-turn encode cache.**

**Correction to the earlier draft (the cache is gone).** The first design added a
content-hash cache for `file_read` on the premise that those images are re-read
from their original on-disk path every turn. They are not: `MaterializeModelInputFiles`
calls `SessionMediaStore.CopyFile`, copying the image **into session media on
first use** — after which it is a `SerializableMediaReference` rehydrated from
media exactly like a chat attachment. The media store *is* the dedup; a cache
would only de-duplicate an agent explicitly `file_read`-ing the *same* image on
two separate turns (a rare micro-optimization, not the OOM fix). Dropped for
simplicity.

Alternative considered: normalize at the egress read (`ToAiMessage`) instead of
the writers. Rejected — it would re-normalize on every turn (the exact per-turn
cost we are removing) and would need a cache to claw it back; normalizing at the
write boundary is strictly simpler and bounds the persisted artifact too.

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

### D5: Format policy — resize only, never transcode (revised after review)

The normalizer **only resizes**. An oversized image is downscaled to the long-edge
cap and re-encoded **in its original container format** — PNG→PNG, JPEG→JPEG,
WebP→WebP. We do **not** transcode (no PNG→JPEG), do **not** quality-reduce, and do
**not** run an iterative byte-shrink loop. Rationale:

- Format conversion is not part of the OOM fix — resizing bounds the decoded
  memory and payload regardless of format. Converting only affects output bytes,
  a secondary concern.
- Transcoding an opaque PNG to JPEG (the original draft) **degrades the common
  case**: screenshots/diagrams/text get ringing artifacts *and* often grow
  (flat content compresses worse as JPEG). Real-world PNGs are overwhelmingly
  flat UI; photos are already JPEG. So "keep the source format" is both
  higher-fidelity and simpler.

The byte budget (~5MB) is therefore a **fail-loud drop gate**, not a shrink
target: an image within the dimension cap but over budget, or a resized image
still over budget, is **dropped** (no transcode/quality escape hatch). Formats
Skia cannot re-encode (**GIF**) are passed through untouched when within budget
(animation preserved) and dropped when over — never decoded-to-still or
transcoded. (Earlier drafts used opacity to pick JPEG-vs-PNG and a JPEG quality
ladder; both were removed by the code review — see the review findings.)

### D8: The memory bound is native and format-dependent (design correction)

Two facts surfaced during implementation that correct the original framing:

- **The bitmap lives in NATIVE memory.** SkiaSharp decodes into Skia's
  unmanaged heap, so `GC.GetAllocatedBytesForCurrentThread()` (the originally
  planned memory assertion) does **not** observe the decode at all. The memory
  guarantee is therefore validated by (a) the unit-tested deterministic
  `ChooseDecodeSampleSize`, which drives `SKCodec.GetScaledDimensions`, plus (b)
  the fail-loud decode ceiling below, plus (c) output-dimension assertions on a
  large fixture — not by a managed-heap counter.
- **Scaled decode is format-dependent.** JPEG (DCT) and WebP support scaled
  decode, so an oversized source of those formats never materializes full-res.
  PNG/GIF/BMP have no native scaled decode — the codec returns full dimensions.
  To keep a pathological huge PNG from OOMing, the normalizer enforces a
  **fail-loud decode ceiling** (`MaxDecodeBytes`, 256 MiB ≈ 8192² RGBA): if the
  scaled decode dimensions would exceed it, the image is **Dropped** rather than
  decoded. So the worst case
  for any input is bounded: either scaled-down on decode, or refused.

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
- **Memory ceiling** — unit-test `ChooseDecodeSampleSize` (pure integer math) +
  an integration test on a large fixture asserting a bounded output. NOTE: no
  `GC.GetAllocatedBytesForCurrentThread()` assertion — Skia decodes into native
  memory the managed GC counter cannot see (see D8); the bound is proven by the
  sample-size math + the decode ceiling + output dimensions instead.
- **Media-store seam** — write/copy an oversized image through the two
  `SessionMediaStore` writers, read the stored artifact back, assert it is
  bounded and that non-image media is unchanged. Extend `ChatMessageConverterTests`
  for the drop-with-note path.
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
- **Lossy re-encode degrades a text-heavy screenshot** → resolved by D5: we never
  transcode, so a PNG screenshot stays a lossless PNG (only resized). 1568px long
  edge keeps text legible (Anthropic's own recommendation).
- **Behavior drift between the two writers** → both call the *same*
  `IImageNormalizer` with the same constant bounds. No second implementation,
  no per-path divergence.
- **Persistence/recovery** → the normalized artifact replaces the original at the
  media-store write, so the stored `SerializableMediaReference` already points at
  bounded bytes; recovery rehydrates it unchanged. No `MediaReference` shape
  change and nothing to lose on restart (no in-process cache).
- **`CopyFile` does more work now** → it currently does a plain file copy; for
  images it becomes decode+normalize+write. This is once per `file_read` handoff,
  not per turn, and is the price of bounding the artifact at the source.

## Migration Plan

1. Add SkiaSharp + Linux native asset; wire into Dockerfile; add load probe. *(done: package)*
2. Land `IImageNormalizer` + `ImageDecodeMath` in `Netclaw.Media` with unit
   tests (no wiring yet). *(done)*
3. Hook normalization into `SessionMediaStore.WriteDataContent` and `CopyFile`
   (gated to images; non-image passthrough); persist the bounded artifact.
4. Media-store seam tests + drop-with-note; eval cases + benchmark before/after
   memory number for the PR.

Rollback: revert the change — the bounds are constants with no runtime switch, by
design (a switch that disabled normalization would re-open the OOM). The
native-asset add is additive.

## Measured impact

One-off measurement through the implemented normalizer (default caps: 1568px /
5MB). The headline is the **native decoded-bitmap** memory — the OOM metric —
which now scales with the target, not the source, and is paid **once at
ingestion** instead of on every turn the image stays in the window. The decoded
column is independent of output format (resize is identical); the output keeps
the **source container format** (resized) per D5:

| Source | Decoded bitmap (full → shrink-on-decode) | Output (resized, source format) |
|--------|------------------------------------------|----------------|
| 8000×8000 (64MP) | **244 MB → 15 MB** (sample 1/4) | 1568×1568 |
| 6000×4000 (24MP) | **92 MB → 23 MB** (sample 1/2) | 1568×1045 |
| 3840×2160 (4K)   | **32 MB → 8 MB** (sample 1/2)  | 1568×882 |

This is the *per-image* peak; pre-change a single message could admit 10 files
and re-materialize all of them every turn. (BenchmarkDotNet was not used — its
`MemoryDiagnoser` only sees managed allocations, not the native Skia bitmap; see
tasks.md 5.2.)

## Open Questions

- Exact default constants — confirm 1568px / 5MB (JPEG quality only applies when a
  JPEG source is resized; default 85).

_Resolved during implementation:_ `CopyFile`/`WriteDataContent` build the
`SerializableMediaReference` from the **written (resized) bytes' length and the
normalizer's reported MIME**, not the source `FileInfo` — verified. The MIME stays
the source container format (resize only, no transcode).
