## 1. Dependency + packaging

- [x] 1.1 Add SkiaSharp + `SkiaSharp.NativeAssets.Linux.NoDependencies` (and win/macOS native assets) to `Directory.Packages.props`; reference from `Netclaw.Media`.
- [x] 1.2 No `docker/Dockerfile` change needed: the daemon publishes self-contained single-file with `IncludeNativeLibrariesForSelfExtract=true`, which embeds `libSkiaSharp.so` and self-extracts at runtime (same mechanism as `e_sqlite3`); the single-binary `COPY` already carries it. RIDs (linux-x64/arm64, win-x64, osx-arm64) are all covered by the SkiaSharp native packages. (Base image is actually `ubuntu:24.04`, glibc — also fine for `NativeAssets.Linux.NoDependencies`.) Also added the same flag to `Netclaw.Cli.csproj` so the doctor probe's native lib ships in the CLI binary too.
- [x] 1.3 `netclaw doctor` "Image Processing" check (`ImageProcessingDoctorCheck` → `ImageNormalizerProbe.TryProbe`) runs an encode→normalize round-trip and reports **Error** with remediation if the native lib fails to load. Tests in `Netclaw.Media.Tests` + `Netclaw.Cli.Tests`.

## 2. Core normalizer (Netclaw.Media, no wiring)

- [x] 2.1 Add pure `ChooseDecodeSampleSize(srcW, srcH, longEdgeCap) -> int` with unit tests (8000×8000 @1568 → sample-size that decodes ≈2000px; already-small → 1; determinism). [`ImageDecodeMath`]
- [x] 2.2 Add `IImageNormalizer` + SkiaSharp impl: shrink-on-decode via `SKCodec` scaled dimensions, long-edge cap + byte-budget iterative shrink (bounded steps), returning `{ outcome, bytes?, width, height, encodedByteLength, mediaType, reason? }`; throws nothing on bad input. [`SkiaImageNormalizer`]
- [x] 2.3 Implement format policy (D5): **resize only, keep source container format** (PNG→PNG, JPEG→JPEG, WebP→WebP) — no transcode, no quality ladder. GIF (and other non-re-encodable formats) pass through when within budget, else drop. Fail-loud 256MiB decode ceiling for formats the codec cannot scale on load. (Original JPEG-for-opaque + quality-ladder approach was removed by the code review — see design.md D5.)
- [x] 2.4 Unit-test transforms: oversized-by-dimension (cap + aspect), oversized-by-bytes (under budget + terminates), already-small (no upscale), corrupt/non-image → `Dropped`, un-shrinkable → `Dropped`. Generate fixtures in-test with SkiaSharp.
- [x] 2.5 Memory-ceiling integration test (`Large_jpeg_source_is_bounded_via_scaled_decode`). NOTE: the planned `GC.GetAllocatedBytesForCurrentThread()` assertion was dropped — Skia decodes into NATIVE memory, which the managed GC counter does not observe. The bound is instead guaranteed by the unit-tested sample-size math + the fail-loud decode ceiling + output-dimension assertions. See design.md.

## 3. Single media-store seam (both writers, no cache, no config)

- [x] 3.1 Normalize images inside `SessionMediaStore.WriteDataContent` (chat attachments + persisted message media); only **model-input-eligible** images are bounded (bmp/tiff and non-image media pass through byte-for-byte). Store the resized artifact only; build the `SerializableMediaReference` from the written bytes' length/MIME (the MIME stays the source format — resize only).
- [x] 3.2 Normalize images inside `SessionMediaStore.CopyFile` (the `file_read` model-input handoff via `MaterializeModelInputFiles`); non-image media copies through unchanged. `CopyFile` is now nullable; the caller skips a drop and releases its batch reservation.
- [x] 3.3 Confirm `ChatMessageConverter.ToAiMessage` needs no change — it reads the already-bounded persisted artifact. (No per-turn cache; the media store is the dedup.)
- [x] 3.4 Seam tests (`SessionMediaStoreImageTests`): oversized chat/file_read image bounded + correct MIME; non-image (audio) byte-unchanged passthrough; small image passthrough; undecodable image dropped + nothing written. Also fixed existing fake-fixture tests via `TestImages` (full Actors suite green, 2200).

## 4. Fail-loud drop

- [x] 4.1 No media reference is persisted on a drop (both writers). file_read drops surface via the model-input handoff warning (`RequestedCount > MediaReferences.Count`); chat-attachment drops now surface a visible `[image omitted: <reason>]` note — `WriteDataContent` returns `MediaWriteResult { Reference?, DroppedReason? }`, and `ChannelPipeline` + `ChatMessageConverter.FromAiMessage` append the note (distinguishing a silent skip for non-media/empty from a drop).
- [x] 4.2 Tests: `SessionMediaStoreImageTests` (drop → no reference + reason carried + nothing written; bounded → correct MIME) and `ChatMessageConverterTests.FromAiMessage_appends_omitted_note_when_image_is_dropped` (note appended + original text preserved + no media ref).

## 5. Quality gates + docs

- [x] 5.1 **Eval cases: not needed / not triggered.** The eval suite (`evals/run-evals.sh`) tests model behavior against a live model and is triggered by changes to identity/skills/memory/compaction/tool-schemas/model-config/SessionConfig (per CLAUDE.md). This change touches none of those — the model-facing contract is unchanged (it still receives an image, just bounded). Determinism is fully covered by unit + integration tests (`LlmSessionImageDeliveryTests` verifies a tool-loaded image reaches the model).
- [x] 5.2 **Checked-in benchmark: not warranted; ran a one-off measurement instead.** BenchmarkDotNet `[MemoryDiagnoser]` measures MANAGED allocations, but the image OOM lives in NATIVE (Skia) memory, so a BDN benchmark would under-report the win (the same native-vs-managed trap as the dropped GC assertion). Normalization is also off the hot path (once per image at ingestion, not per turn/token), so there's no throughput regression to guard. (Contrast: the existing ShellDrain/Capture benchmarks measure managed buffering, where MemoryDiagnoser is the right tool.) One-off numbers recorded in design.md § Measured impact.
- [x] 5.3 **No skill update needed.** The `netclaw-operations` skill gives operational guidance, not a catalog of every doctor check; the new "Image Processing" check is self-describing at `netclaw doctor` runtime. No config knobs to document (bounds are constants).
- [x] 5.4 `dotnet slopwatch analyze` (0 issues) and `./scripts/Add-FileHeaders.ps1 -Verify` (all headers present) — passing.
- [ ] 5.5 `/opsx-verify` then `/opsx-sync` + `/opsx-archive` once implemented and merged.
