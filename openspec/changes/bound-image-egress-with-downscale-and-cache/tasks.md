## 1. Dependency + packaging

- [x] 1.1 Add SkiaSharp + `SkiaSharp.NativeAssets.Linux.NoDependencies` (and win/macOS native assets) to `Directory.Packages.props`; reference from `Netclaw.Media`.
- [x] 1.2 No `docker/Dockerfile` change needed: the daemon publishes self-contained single-file with `IncludeNativeLibrariesForSelfExtract=true`, which embeds `libSkiaSharp.so` and self-extracts at runtime (same mechanism as `e_sqlite3`); the single-binary `COPY` already carries it. RIDs (linux-x64/arm64, win-x64, osx-arm64) are all covered by the SkiaSharp native packages. (Base image is actually `ubuntu:24.04`, glibc — also fine for `NativeAssets.Linux.NoDependencies`.) Also added the same flag to `Netclaw.Cli.csproj` so the doctor probe's native lib ships in the CLI binary too.
- [x] 1.3 `netclaw doctor` "Image Processing" check (`ImageProcessingDoctorCheck` → `ImageNormalizerProbe.TryProbe`) runs an encode→normalize round-trip and reports **Error** with remediation if the native lib fails to load. Tests in `Netclaw.Media.Tests` + `Netclaw.Cli.Tests`.

## 2. Core normalizer (Netclaw.Media, no wiring)

- [x] 2.1 Add pure `ChooseDecodeSampleSize(srcW, srcH, longEdgeCap) -> int` with unit tests (8000×8000 @1568 → sample-size that decodes ≈2000px; already-small → 1; determinism). [`ImageDecodeMath`]
- [x] 2.2 Add `IImageNormalizer` + SkiaSharp impl: shrink-on-decode via `SKCodec` scaled dimensions, long-edge cap + byte-budget iterative shrink (bounded steps), returning `{ outcome, bytes?, width, height, encodedByteLength, mediaType, reason? }`; throws nothing on bad input. [`SkiaImageNormalizer`]
- [x] 2.3 Implement format policy (D5): JPEG re-encode for photos at constant quality; preserve PNG for alpha/lossless; passthrough already-bounded supported formats without re-encode. (Refinement: added a JPEG quality ladder + a fail-loud 256MiB decode ceiling for formats the codec cannot scale on load — see design.md.)
- [x] 2.4 Unit-test transforms: oversized-by-dimension (cap + aspect), oversized-by-bytes (under budget + terminates), already-small (no upscale), corrupt/non-image → `Dropped`, un-shrinkable → `Dropped`. Generate fixtures in-test with SkiaSharp.
- [x] 2.5 Memory-ceiling integration test (`Large_jpeg_source_is_bounded_via_scaled_decode`). NOTE: the planned `GC.GetAllocatedBytesForCurrentThread()` assertion was dropped — Skia decodes into NATIVE memory, which the managed GC counter does not observe. The bound is instead guaranteed by the unit-tested sample-size math + the fail-loud decode ceiling + output-dimension assertions. See design.md.

## 3. Single media-store seam (both writers, no cache, no config)

- [x] 3.1 Normalize images inside `SessionMediaStore.WriteDataContent` (chat attachments + persisted message media); non-image media passes through unchanged. Store the normalized artifact only; build the `SerializableMediaReference` from the written bytes' length/MIME (PNG→JPEG may change the MIME).
- [x] 3.2 Normalize images inside `SessionMediaStore.CopyFile` (the `file_read` model-input handoff via `MaterializeModelInputFiles`); non-image media copies through unchanged. `CopyFile` is now nullable; the caller skips a drop and releases its batch reservation.
- [x] 3.3 Confirm `ChatMessageConverter.ToAiMessage` needs no change — it reads the already-bounded persisted artifact. (No per-turn cache; the media store is the dedup.)
- [x] 3.4 Seam tests (`SessionMediaStoreImageTests`): oversized chat/file_read image bounded + correct MIME; non-image (audio) byte-unchanged passthrough; small image passthrough; undecodable image dropped + nothing written. Also fixed existing fake-fixture tests via `TestImages` (full Actors suite green, 2200).

## 4. Fail-loud drop

- [x] 4.1 No media reference is persisted on a drop (both writers). file_read drops surface via the model-input handoff warning (`RequestedCount > MediaReferences.Count`); chat-attachment drops now surface a visible `[image omitted: <reason>]` note — `WriteDataContent` returns `MediaWriteResult { Reference?, DroppedReason? }`, and `ChannelPipeline` + `ChatMessageConverter.FromAiMessage` append the note (distinguishing a silent skip for non-media/empty from a drop).
- [x] 4.2 Tests: `SessionMediaStoreImageTests` (drop → no reference + reason carried + nothing written; bounded → correct MIME) and `ChatMessageConverterTests.FromAiMessage_appends_omitted_note_when_image_is_dropped` (note appended + original text preserved + no media ref).

## 5. Quality gates + docs

- [ ] 5.1 Add 1–2 eval-suite image cases (tool image discovery/use) as the end-to-end backstop; run `./evals/run-evals.sh`.
- [ ] 5.2 One-off BenchmarkDotNet / custom memory harness run capturing before/after peak memory + payload bytes for the PR writeup.
- [ ] 5.3 Update `netclaw-operations` system skill only if operator-facing diagnostics changed (the doctor probe in 1.3); no config knobs to document (bounds are constants).
- [ ] 5.4 Run `dotnet slopwatch analyze` (no new violations) and `./scripts/Add-FileHeaders.ps1 -Verify`.
- [ ] 5.5 `/opsx-verify` then `/opsx-sync` + `/opsx-archive` once implemented and merged.
