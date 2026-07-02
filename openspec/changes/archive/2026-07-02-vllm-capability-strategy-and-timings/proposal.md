# Proposal: vLLM Capability Strategy & Timings Extraction

## Why

Netclaw is now running against a real vLLM 0.20 instance serving Qwen3.6-VL
through the `openai-compatible` provider type, and two capability-resolution
bugs surface as user-visible failures
([issue #619](https://github.com/netclaw-dev/netclaw/issues/619),
gaps #1 and #3):

1. **Context window misreported as 32,768** (the default fallback) when the
   real `max_model_len` is 256,000 — the resolver reads `meta.n_ctx_train`
   (a llama.cpp extension) and ignores vLLM's top-level `max_model_len`.
2. **Vision-capable models advertised as text-only** — vLLM has no `/props`
   endpoint and exposes no modality field on `/v1/models`, so the resolver
   defaults to `Text`. Slack image-bearing turns are silently dropped because
   `InputModalities.HasFlag(Image)` is false.

A latent observability regression also lands on cutover: the
llama.cpp-specific `timings` parser in `OpenAiCompatibleChatClient` silently
produces zero `cached_tokens` against vLLM, breaking the Multi-Turn Cache
Evolution eval table. vLLM exposes the equivalent data via
`usage.prompt_tokens_details.cached_tokens` (OpenAI-standard prefix-cache
field).

Root architectural cause: `CompositeCapabilityResolver` short-circuits on the
**first non-null** result. `OpenAiCompatibleCapabilityResolver` returns
`(modelId, Text, Text, null)` for vLLM — non-null but wrong — so the
already-registered `HuggingFaceCapabilityResolver` is never consulted to fill
the modality gap.

## What Changes

- **Composite merging** (BREAKING for resolver authors): change
  `CompositeCapabilityResolver` from short-circuit-on-first-non-null to
  field-merge across all resolvers. Each resolver returns a partial answer;
  the composite walks the chain merging the first non-null value per field.
- **Provider-aware resolver scoping** (BREAKING for resolver authors):
  add `string? ProviderType { get; }` to `IModelCapabilityResolver`.
  Provider-native resolvers tag themselves (`"openai"`, `"ollama"`,
  `"openai-compatible"`); cross-provider oracles return `null`. The
  composite filters out resolvers whose `ProviderType` doesn't match the
  active model's `ModelReference.Provider`, so foreign native resolvers
  never burn network probes. Necessary once field-merge lands and the
  testlab cutover plan runs llama.cpp + vLLM in parallel as separate
  `openai-compatible` providers.
- **Nullable modalities on `ResolvedModelCapabilities`** (BREAKING for
  resolver authors): change `InputModalities` / `OutputModalities` from
  `ModelModality` to `ModelModality?` so resolvers can signal "I don't know"
  rather than falsely advertising `Text`. The text-only default already
  lives downstream in `ModelCapabilityResolution.cs` and remains the
  user-visible default when every resolver returns null.
- **Backend strategy pattern** inside `OpenAiCompatibleCapabilityResolver`:
  refactor parsing into `IOpenAiBackendStrategy` with three implementations
  evaluated in order — `VllmBackendStrategy` (parses `max_model_len` from
  the top-level model entry), `LlamaCppBackendStrategy` (parses
  `meta.n_ctx_train` + `/props.modalities.vision`), and
  `GenericOpenAiBackendStrategy` (last-resort identity). Single probe of
  `/v1/models` and `/props` is shared across strategies.
- **Pluggable timings extraction**: split `ParseLlamaCppTimings` into
  `ITimingsExtractor` with `LlamaCppTimingsExtractor` (existing behaviour
  against the `timings` object) and `VllmTimingsExtractor` (reads
  `usage.prompt_tokens_details.cached_tokens`). Both extractors run on every
  response since the field paths don't conflict.
- **Wall-clock `prompt_ms`** in `OpenAiCompatibleChatClient`: measure HTTP
  send → first-byte and populate `UsageDetails.PromptMs` when the server
  didn't supply its own value. Restores per-request latency telemetry on
  backends that don't emit it.

## Capabilities

### New Capabilities

_None._ Existing capabilities cover this work — the strategy pattern,
nullable modalities, and timings split are spec-level refinements of
already-specified behaviour.

### Modified Capabilities

- `netclaw-model-capabilities`: relaxes "ModelModality default text-only"
  to apply at the final consumption boundary, not at every resolver. Adds
  composite-merge semantics. Adds backend-strategy enumeration for the
  OpenAI-compatible resolver (vLLM, llama.cpp, generic).

## Impact

**Code**
- `src/Netclaw.Configuration/IModelCapabilityResolver.cs` — nullable
  modalities on `ResolvedModelCapabilities`.
- `src/Netclaw.Daemon/Providers/CompositeCapabilityResolver.cs` —
  merge-across-resolvers replacing short-circuit. Removes the text-only
  final fallback (already handled downstream).
- `src/Netclaw.Providers/SelfHosted/OpenAiCompatibleCapabilityResolver.cs`
  — strip parse logic; delegate to strategies.
- NEW `src/Netclaw.Providers/SelfHosted/OpenAiBackendStrategy.cs` — holds
  interface + 3 strategy implementations (single file per CLAUDE.md
  "group closely related types" rule).
- `src/Netclaw.Providers/SelfHosted/OpenAiCompatibleChatClient.cs` — call
  `ITimingsExtractor`s in sequence; capture wall-clock `prompt_ms`.
- NEW `src/Netclaw.Providers/SelfHosted/TimingsExtractor.cs` — holds
  interface + 2 timing extractor implementations.
- Callers of `ResolvedModelCapabilities.InputModalities` /
  `.OutputModalities` updated for nullable: `OpenAiCodexCapabilityResolver`,
  `DaemonRuntimeStatusService`, capability log lines in `Program.cs`.

**APIs / Contracts**
- `IModelCapabilityResolver` contract unchanged in shape; semantic change
  is that implementations are now expected to return partial answers
  (null fields allowed) rather than fully-populated records. Downstream
  consumers already null-coalesce.

**Operational**
- Live vLLM deployments will start reporting correct context window and
  vision capability without any config change.
- Multi-Turn Cache Evolution eval metric starts populating on vLLM
  backends.
- Per-request latency telemetry is restored when the backend doesn't emit
  server-side timings (covers any future OpenAI-compatible backend, not
  just vLLM).

**Tests**
- New `VllmBackendStrategyTests`, `LlamaCppBackendStrategyTests`,
  `CompositeCapabilityResolverMergeTests`, `VllmTimingsExtractorTests`,
  `LlamaCppTimingsExtractorTests`.
- Existing `OpenAiCompatibleCapabilityResolverTests` updated to assert
  partial-result semantics.

**Security**
- No new auth surface. HuggingFace fallback already exists and runs
  network requests against `huggingface.co`; this change increases how
  often it's actually consulted but does not introduce new outbound
  endpoints.

**Out of scope** (tracked in #619, deferred to follow-up changes):
- Gap #2 vLLM tool-call parser quirks (`hermes` streaming bug,
  `qwen3_coder` parser variant).
- Gap #4 strict `model` field diagnostics.
- Gaps #5–#7 error shape / stop-sequence / response-extras tolerance.

**PRD reference**: this change has no direct PRD — it's a faithful
implementation refinement of existing `netclaw-model-capabilities`
behaviour to handle a second supported backend without regressing the
first.
