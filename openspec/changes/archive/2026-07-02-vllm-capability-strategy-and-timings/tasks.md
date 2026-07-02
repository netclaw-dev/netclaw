# Tasks: vLLM Capability Strategy & Timings Extraction

## 1. Capability contract — make modalities nullable

- [x] 1.1 Change `ResolvedModelCapabilities.InputModalities` and
  `OutputModalities` in
  `src/Netclaw.Configuration/IModelCapabilityResolver.cs` from
  `ModelModality` to `ModelModality?` (default `null`).
- [x] 1.2 Update `OpenAiCodexCapabilityResolver` constructor (it builds
  records from a known catalog — keep non-null).
- [x] 1.3 Update `DaemonRuntimeStatusService` so it null-coalesces to
  `ModelModality.Text` before `.ToString()`-ing for display.
- [x] 1.4 Update the three resolver-success log lines in
  `Program.cs:1219-1261` so they print `"unknown"` (or similar) when
  modality fields are null.
- [x] 1.5 Confirm `ModelCapabilityResolution.ResolveModelCapabilities`
  (`src/Netclaw.Daemon/Configuration/ModelCapabilityResolution.cs:34-36`)
  still compiles and behaves correctly under `ModelModality?` inputs
  — it already null-coalesces.

## 2. Backend strategy seam for OpenAI-compatible

- [x] 2.1 Create
  `src/Netclaw.Providers/SelfHosted/OpenAiBackendStrategy.cs` holding:
  - `BackendProbe` record (raw `/v1/models` JSON + nullable `/props`
    JSON + the served model id).
  - `IOpenAiBackendStrategy` interface
    (`bool Matches(BackendProbe probe)` /
    `ResolvedModelCapabilities? Parse(BackendProbe probe, string modelId)`).
  - `VllmBackendStrategy`: matches on `owned_by == "vllm"` OR
    `max_model_len` present AND `/props` was 404. Parses
    `max_model_len`; leaves modalities null.
  - `LlamaCppBackendStrategy`: matches on `/props` 200 OR
    `meta.n_ctx_train` present. Reads `/props.n_ctx` preferentially
    over `meta.n_ctx_train`; reads `/props.modalities.vision` for
    image input.
  - `GenericOpenAiBackendStrategy`: always matches as last-resort;
    returns ModelId only.
- [x] 2.2 Wire strategies into the existing
  `OpenAiCompatibleCapabilityResolver`: probe `/v1/models` and `/props`
  once, build `BackendProbe`, iterate strategies in
  `[Vllm, LlamaCpp, Generic]` order.
- [x] 2.3 Add an info-level log line emitting the matched backend
  strategy name when capabilities are resolved (per Open Question in
  design doc — diagnostic value, free of cost).

## 2b. Provider-aware resolver scoping

- [x] 2b.1 Add `string? ProviderType { get; }` to
  `IModelCapabilityResolver` in
  `src/Netclaw.Configuration/IModelCapabilityResolver.cs`.
- [x] 2b.2 Set `ProviderType` on each in-tree resolver:
  - `OpenAiCodexCapabilityResolver` → `"openai"`
  - `OllamaCapabilityResolver` → `"ollama"`
  - `OpenAiCompatibleCapabilityResolver` → `"openai-compatible"`
  - `OpenRouterOracleResolver` → `null` (oracle)
  - `HuggingFaceCapabilityResolver` → `null` (oracle)
- [x] 2b.3 Pipe the active model's `ModelReference.Provider` into the
  composite resolver call. Two options to evaluate during
  implementation: (a) thread `activeProvider` through
  `IModelCapabilityResolver.ResolveAsync(modelId, activeProvider, ct)`,
  or (b) bind `activeProvider` once at composite construction via a
  factory. (a) is cleaner; verify it doesn't ripple to too many
  callers.
- [x] 2b.4 In `CompositeCapabilityResolver`, filter eligible resolvers
  by `ProviderType is null || ProviderType == activeProvider` **before**
  the merge walk.
- [x] 2b.5 Log at debug-level any resolver skipped due to provider
  mismatch (one line, includes resolver type name + reason). Cheap
  diagnostic, no info-level noise.

## 3. Composite resolver field-merge

- [x] 3.1 Replace the short-circuit logic in
  `src/Netclaw.Daemon/Providers/CompositeCapabilityResolver.cs:34-66`
  with field-merge across all resolvers (first non-null wins per
  field).
- [x] 3.2 Remove the unconditional text-only default at the chain end
  — return the merged partial; downstream
  `ModelCapabilityResolution` handles defaulting.
- [x] 3.3 Preserve per-resolver 5-second timeout + warning-on-exception
  behavior already in the file.

## 4. Timings extractor split

- [x] 4.1 Create `src/Netclaw.Providers/SelfHosted/TimingsExtractor.cs`
  holding:
  - `ITimingsExtractor` interface
    (`void Extract(JsonElement root, UsageDetails details)`).
  - `LlamaCppTimingsExtractor`: relocate the body of
    `ParseLlamaCppTimings` from `OpenAiCompatibleChatClient.cs:699-715`.
  - `VllmTimingsExtractor`: reads
    `usage.prompt_tokens_details.cached_tokens` into
    `UsageDetails.CachedInputTokens`.
- [x] 4.2 In `OpenAiCompatibleChatClient`, replace the direct
  `ParseLlamaCppTimings` call at line 675 with a sequence over both
  extractors. Order: llama.cpp first, vLLM second (idempotent
  regardless of order; this matches existing test data shape).

## 5. Wall-clock prompt_ms fallback

- [x] 5.1 In `OpenAiCompatibleChatClient.GetResponseAsync` and
  `.GetStreamingResponseAsync`, capture a timestamp immediately
  before `_httpClient.SendAsync(...)` and a second timestamp after
  the first content byte is observed.
- [x] 5.2 After both timings extractors have run, set
  `UsageDetails.PromptMs` to the wall-clock value **only** if
  `details.PromptMs` is still null. Document in code that the
  wall-clock variant includes network round-trip.

## 6. Tests

- [x] 6.1 Create
  `src/Netclaw.Daemon.Tests/Providers/Strategies/VllmBackendStrategyTests.cs`
  with the real vLLM `/v1/models` shape from the May-13 field repro
  (`owned_by: "vllm"`, `max_model_len: 256000`). Assert: strategy
  matches, returns `ContextWindowTokens = 256000`, null modalities.
- [x] 6.2 Create
  `src/Netclaw.Daemon.Tests/Providers/Strategies/LlamaCppBackendStrategyTests.cs`.
  Migrate existing `ParseModelsResponse`/`ParsePropsResponse` fixtures
  from `OpenAiCompatibleCapabilityResolverTests`. Cover:
  `meta.n_ctx_train` only; `/props` only; both
  (assert `/props.n_ctx` wins); `modalities.vision: true` → `Image`.
- [x] 6.3 Create
  `src/Netclaw.Daemon.Tests/Providers/Strategies/GenericOpenAiBackendStrategyTests.cs`.
  Cover: returns `(modelId, null, null, null)`; matches when other
  strategies don't.
- [x] 6.4 Create
  `src/Netclaw.Daemon.Tests/Providers/CompositeCapabilityResolverMergeTests.cs`.
  Cover: vLLM-shaped + HF-shaped merge to full record; first
  non-null per field; all-null inputs propagate as all-null output
  (no default injected by composite); resolver with mismatched
  `ProviderType` skipped (no `ResolveAsync` invocation); resolver with
  `ProviderType == null` runs for any active provider.
- [x] 6.5 Create
  `src/Netclaw.Daemon.Tests/Providers/VllmTimingsExtractorTests.cs`.
  Real vLLM response shape (`usage.prompt_tokens_details.cached_tokens`)
  → `UsageDetails.CachedInputTokens`.
- [x] 6.6 Create
  `src/Netclaw.Daemon.Tests/Providers/LlamaCppTimingsExtractorTests.cs`.
  Move any existing `ParseLlamaCppTimings` coverage. Verify both
  `timings.cache_n` and `timings.prompt_ms` round-trip.
- [x] 6.7 Update
  `src/Netclaw.Daemon.Tests/Providers/OpenAiCompatibleCapabilityResolverTests.cs`:
  delete tests now duplicated in strategy-level test classes; add a
  high-level test that the resolver delegates to the right strategy
  given a backend-probe fixture.

## 7. Spec sync + quality gates

- [x] 7.1 `dotnet build` clean from repo root.
- [x] 7.2 `dotnet test src/Netclaw.Daemon.Tests/Netclaw.Daemon.Tests.csproj`
  passes (full suite, not just `Providers` filter — sanity check
  callers of the changed contract).
- [x] 7.3 `dotnet slopwatch analyze` reports no new violations.
- [x] 7.4 `./scripts/Add-FileHeaders.ps1 -Verify` passes on every new
  `.cs` file.
- [x] 7.5 Run `openspec validate vllm-capability-strategy-and-timings`
  and fix any reported issues.

## 8. End-to-end verification against live vLLM

- [ ] 8.1 Run `netclaw models --provider <vllm-local>` and confirm
  `ContextWindow=256000` and `Input=Text|Image` for the Qwen3.6-VL
  model.
- [ ] 8.2 Send a Slack thread image-bearing turn through the vLLM
  provider and confirm the image is inlined (not stripped).
- [ ] 8.3 Drive a multi-turn session and confirm
  `[usage] cached=<non-zero>` lines appear in
  `HeadlessChannel.cs:271` output on turn 2+.
- [ ] 8.4 Repoint the same provider at a llama-server endpoint and
  confirm no regression: existing
  `meta.n_ctx_train`/`/props`/`timings.cache_n` paths still surface.

## 9. Wrap-up

- [ ] 9.1 Run `/opsx-verify vllm-capability-strategy-and-timings` to
  ensure code matches spec.
- [ ] 9.2 Run `/opsx-sync vllm-capability-strategy-and-timings` to merge
  delta into `openspec/specs/netclaw-model-capabilities/spec.md`.
- [ ] 9.3 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md`
  if any operator-visible diagnostic changes (e.g., the new "detected
  backend = vLLM" log line). Bump skill `metadata.version`.
- [ ] 9.4 Run `/opsx-archive vllm-capability-strategy-and-timings`
  after merge.
