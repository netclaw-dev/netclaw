## 1. Dependency + packaging

- [ ] 1.1 Add SkiaSharp + `SkiaSharp.NativeAssets.Linux.NoDependencies` (and win/macOS native assets) to `Directory.Packages.props`; reference from `Netclaw.Media`.
- [ ] 1.2 Wire the Linux native asset into `docker/Dockerfile` (Debian bookworm-slim, glibc) and confirm it restores for the published RID.
- [ ] 1.3 Add a startup/`doctor` probe that the SkiaSharp native lib loads, failing loud on a packaging regression (no silent skip).

## 2. Core normalizer (Netclaw.Media, no wiring)

- [ ] 2.1 Add pure `ChooseDecodeSampleSize(srcW, srcH, longEdgeCap) -> int` with unit tests (8000×8000 @1568 → sample-size that decodes ≈2000px; already-small → 1; determinism).
- [ ] 2.2 Add `IImageNormalizer` + SkiaSharp impl: shrink-on-decode via `SKCodec` scaled dimensions, long-edge cap + byte-budget iterative shrink (bounded steps), returning `{ outcome, bytes?, width, height, encodedByteLength, mediaType, reason? }`; throws nothing on bad input.
- [ ] 2.3 Implement format policy (D5): JPEG re-encode for photos at configurable quality; preserve PNG for alpha/lossless; passthrough already-bounded supported formats without re-encode.
- [ ] 2.4 Unit-test transforms: oversized-by-dimension (cap + aspect), oversized-by-bytes (under budget + terminates), already-small (no upscale), corrupt/non-image → `Dropped`, un-shrinkable → `Dropped`. Generate fixtures in-test with SkiaSharp.
- [ ] 2.5 Memory-ceiling integration test: decode an 8000×8000 fixture, assert decoded buffer ≈ target size; coarse `GC.GetAllocatedBytesForCurrentThread()` upper-bound assertion.

## 3. Configuration + schema

- [ ] 3.1 Add image-egress `*Config` block (long-edge cap, byte budget, JPEG quality, enabled) to `Netclaw.Configuration` with safe defaults.
- [ ] 3.2 Update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` in the same PR (defaults present, `additionalProperties:false` compatible, `doctor --fix` friendly).
- [ ] 3.3 Config-load tests: block present (caps honored) and absent (schema defaults validate, no `additionalProperties` violation).

## 4. file_read egress path + content-hash cache (Codex model)

- [ ] 4.1 Add a content-hash-keyed encoded-image cache (LRU bounded by count/total bytes; inject `TimeProvider` if any TTL).
- [ ] 4.2 Route `FileReadTool` `AddModelInputFile` / model-input materialization through the normalizer + cache instead of handing raw on-disk bytes to egress.
- [ ] 4.3 Cache tests with a spy normalizer: same bytes → one invocation, different bytes → two, eviction over capacity.

## 5. Chat-attachment ingestion path (OpenCode model)

- [ ] 5.1 Normalize in/around `SessionMediaStore.WriteDataContent` so the persisted artifact is already bounded (store normalized-only; original not retained).
- [ ] 5.2 Confirm `ChatMessageConverter.ToAiMessage` now reads a bounded artifact; minimal change there. If `MediaReference` gains a normalized marker, keep it serialization round-trip safe + add a round-trip test.
- [ ] 5.3 Ingestion test: admit oversized image, read stored artifact back, assert bounded; later egress reads do not re-run the normalizer.

## 6. Fail-loud drop

- [ ] 6.1 On `Dropped`, emit `[image omitted: <reason>]` text content and attach no image bytes (egress + ingestion). Surface ingestion drops at admission where possible.
- [ ] 6.2 Tests (extend `ChatMessageConverterTests`): undecodable ref → note + no `DataContent`; un-shrinkable → note, raw bytes never attached; bounded image → `DataContent.Data.Length` within budget + correct media type.

## 7. Quality gates + docs

- [ ] 7.1 Add 1–2 eval-suite image cases (tool image discovery/use) as the end-to-end backstop; run `./evals/run-evals.sh`.
- [ ] 7.2 One-off BenchmarkDotNet / custom memory harness run capturing before/after peak memory + payload bytes for the PR writeup.
- [ ] 7.3 Document the new config knobs in CLI help / operations runbook; update `netclaw-operations` system skill if config/diagnostics guidance changed.
- [ ] 7.4 Run `dotnet slopwatch analyze` (no new violations) and `./scripts/Add-FileHeaders.ps1 -Verify`.
- [ ] 7.5 `/opsx-verify` then `/opsx-sync` + `/opsx-archive` once implemented and merged.
