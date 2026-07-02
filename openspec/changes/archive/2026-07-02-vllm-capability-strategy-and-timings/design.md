# Design: vLLM Capability Strategy & Timings Extraction

## Context

The `openai-compatible` provider type today serves two real backends —
llama.cpp's `llama-server` and (newly) vLLM 0.20. They share the
OpenAI-standard endpoint shape but diverge on every capability /
telemetry extension:

| Concern               | llama.cpp                              | vLLM                                                  |
|-----------------------|----------------------------------------|-------------------------------------------------------|
| Context window source | `/v1/models[].meta.n_ctx_train` + `/props.default_generation_settings.params.n_ctx` | `/v1/models[].max_model_len`                          |
| Modality signal       | `/props.modalities.vision`             | _none on `/v1/models` or `/props` (404)_              |
| Prefix-cache stat     | `timings.cache_n`                      | `usage.prompt_tokens_details.cached_tokens` (OpenAI-std) |
| Prompt latency        | `timings.prompt_ms`                    | _none — must be measured client-side_                 |

The existing resolver was written when llama.cpp was the only target.
It also short-circuits the composite chain on first non-null result —
which means the already-registered `HuggingFaceCapabilityResolver`,
which would correctly identify Qwen3.6-VL as `Text|Image`, never runs
when the OpenAI-compatible resolver returns its (wrong) text-only
answer for vLLM.

This design refactors capability detection along two axes —
**backend-aware parsing** (strategies inside the OpenAI-compatible
resolver) and **partial-answer composition** (field-merge in the
composite resolver) — so adding new OpenAI-compatible backends in the
future is a matter of writing one strategy class, not patching parsing
heuristics across the codebase.

## Goals / Non-Goals

**Goals:**

- Detect correct context window on vLLM (`max_model_len`) without
  regressing llama.cpp detection (`meta.n_ctx_train`, `/props.n_ctx`).
- Detect correct modality on vLLM by routing the unresolved-modality
  case to `HuggingFaceCapabilityResolver`, which already knows how to
  map `pipeline_tag` to `ModelModality` flags.
- Keep `usage.cached_tokens` flowing into eval-suite Multi-Turn Cache
  Evolution metrics when running on vLLM.
- Establish a strategy seam where future per-backend quirks (tool-call
  streaming parsers, strict `model` field diagnostics) can land
  cleanly.
- Preserve the existing user-facing escape hatch:
  `ModelReference.InputModalities` / `OutputModalities` /
  `ContextWindow` overrides still take precedence over all detection.

**Non-Goals:**

- Solving the remaining gaps from issue #619 (tool-call parser quirks,
  strict model-field diagnostics, error shape alignment, stop-sequence
  semantics). Those land in follow-up changes.
- Active vision-capability probing (sending a 1-pixel image to
  `/v1/chat/completions` at startup). HuggingFace fallback covers the
  common case; we can revisit this if config burden grows for
  HF-orphan model ids.
- Persisting backend identification (vLLM vs llama.cpp) anywhere
  durable. The probe runs each time the resolver is invoked; the
  capability cache actor handles caching the result.

## Decisions

### D1. One resolver, multiple strategies (vs one resolver per backend)

**Choice:** Keep a single `OpenAiCompatibleCapabilityResolver` registered
in the composite chain. Refactor its parse logic into an internal
`IOpenAiBackendStrategy` interface with three implementations
(`Vllm`, `LlamaCpp`, `Generic`). The resolver performs **one** HTTP
probe (`/v1/models` + `/props`), wraps results in a `BackendProbe`
record, and walks strategies in priority order — first to match wins.

**Alternatives considered:**

- _Three sibling resolvers registered in the composite chain
  (`VllmCapabilityResolver`, `LlamaCppCapabilityResolver`,
  `GenericOpenAiCompatibleCapabilityResolver`)._ Cleaner DI surface but
  each resolver would re-probe the same endpoint, tripling network
  calls on every cache miss. Composite-merge would naturally pick the
  right backend's fields, but at 3× the request load.
- _Single resolver with `if/else` branching in `ParseModelsResponse`._
  Works for two backends; gets hairy at three or four. Strategy
  pattern keeps the dispatch readable and tested per-backend.

**Trade-off accepted:** Strategies live inside the
`Netclaw.Providers.SelfHosted` namespace alongside the resolver
(single-file grouping per CLAUDE.md "do NOT enforce one type per
file"). DI registration stays at the resolver level; strategies are
constructor-injected as `IEnumerable<IOpenAiBackendStrategy>` so they
remain unit-testable.

### D2. Composite resolver merges instead of short-circuiting

**Choice:** `CompositeCapabilityResolver` walks **every** resolver in
the chain and merges field-by-field with first-non-null wins. The
existing "default to text-only at the resolver chain" final fallback
moves out — defaulting already lives at the consumption boundary in
`ModelCapabilityResolution.cs:34-36`.

**Why this matters:** The chain today is
`OpenAiCodex → Ollama → OpenAiCompatible → OpenRouter → HuggingFace`.
With short-circuit semantics, `OpenAiCompatible` returning
`(Text, Text, null)` for vLLM ends the walk and HuggingFace never
runs. With field-merge, the same vLLM resolver returning
`(null, null, 256000)` lets HuggingFace fill the modality fields on
the next iteration.

**Alternatives considered:**

- _Have the OpenAI-compatible resolver itself call HuggingFace as a
  helper._ Couples backends tightly and duplicates resolution logic.
- _Add an explicit "I cannot determine modality" sentinel value._
  Discriminating `null` from `Text` is semantically correct and matches
  how `ContextWindowTokens` is already modeled (`int?`).

### D3. Nullable modalities on `ResolvedModelCapabilities`

**Choice:** Change `InputModalities` and `OutputModalities` on
`ResolvedModelCapabilities` from `ModelModality` to `ModelModality?`.
This is a small breaking change to the resolver-author contract;
downstream consumers in `ModelCapabilityResolution`,
`DaemonRuntimeStatusService`, and the `Program.cs` log lines either
already null-coalesce or are trivial to update.

**Alternative considered:** Introduce a separate
`PartialModelCapabilities` type for internal merge use, with public
resolvers continuing to return non-null fields. Rejected — adds a type
without solving the original "I don't know" expression problem at the
resolver boundary.

### D3a. Provider-aware resolver scoping in composite

**Choice:** Add a `string? ProviderType { get; }` property to
`IModelCapabilityResolver`. Provider-native resolvers (`OpenAiCodex`,
`Ollama`, `OpenAiCompatible`) return their provider type literal
(`"openai"`, `"ollama"`, `"openai-compatible"`). Cross-provider oracle
resolvers (`OpenRouterOracle`, `HuggingFace`) return `null`.

The composite resolves the active model's provider from
`ModelReference.Provider` and only invokes resolvers where
`ProviderType is null || ProviderType == activeProvider`. Filtering
happens **before** the merge walk so foreign provider-native resolvers
never burn a network call.

**Why this matters:** Under field-merge semantics (D2), every eligible
resolver runs. The testlab cutover plan keeps llama.cpp and vLLM
running in parallel as separate `openai-compatible` providers — both
native resolvers would be registered. Without scoping, both resolvers
probe both endpoints on every model lookup, with only one returning
useful data. Scoping ties each capability query to the provider that
actually serves the model.

**Alternatives considered:**

- _Per-provider composite chains registered separately in DI._
  Architecturally clean but adds N×M registration combinatorics; the
  scoping property keeps registration flat.
- _Make oracles enumerate every provider they cover._ Forces
  HuggingFace/OpenRouter to list "openai-compatible", "openai",
  "anthropic", etc. — duplicates the cross-cutting intent and rots
  when new providers land.

### D4. Apply both timings extractors on every response (no backend dispatch)

**Choice:** Run `LlamaCppTimingsExtractor` and `VllmTimingsExtractor`
in sequence on every chat response. Field paths don't overlap
(llama.cpp puts cache stats inside the `timings` sibling object; vLLM
puts them inside `usage.prompt_tokens_details`). Whichever shape is
present writes its values; the other extractor finds no fields and
writes nothing.

**Alternative considered:** Reuse the backend strategy from D1 to
pick a single extractor. Rejected as over-engineering — strategy
dispatch makes sense for parse logic with mutually exclusive shapes;
here the shapes are additive and free of conflict.

### D5. Wall-clock `prompt_ms` only as fallback

**Choice:** Capture the elapsed time between HTTP `SendAsync` start and
first-byte-received in both streaming and non-streaming paths. Use it
to populate `UsageDetails.PromptMs` **only** when no extractor set a
server-side value. llama.cpp continues to win on its own backend; vLLM
gets a wall-clock value that's actually more honest than any single
server-reported timing.

**Trade-off:** Wall-clock includes network round-trip, so on a remote
vLLM it overstates server-side prompt processing. Acceptable — the
metric is for observability, not SLA evaluation. We log it tagged as
client-measured.

## Risks / Trade-offs

- **[Risk]** Making modality fields nullable on
  `ResolvedModelCapabilities` is a contract change for any third-party
  resolver implementations. → Mitigation: There are no third-party
  implementations in tree or known out of tree. All in-tree resolvers
  are updated in the same change.
- **[Risk]** `HuggingFace` lookups now run more often (on every vLLM
  modality miss). → Mitigation: HF returns 304 / cached responses fast;
  the capability cache actor (`ModelCapabilityActor`) caches resolved
  results, so HF is hit once per (model, daemon-restart). 5-second
  per-resolver timeout already in `CompositeCapabilityResolver` keeps
  startup bounded.
- **[Risk]** vLLM strategy match predicate (`owned_by: "vllm"` or
  `max_model_len` + `/props` 404) misclassifies a backend that
  partially mimics vLLM. → Mitigation: `max_model_len` is a stable
  vLLM-specific schema field; misclassification would only affect
  parsing fields that vLLM happens to provide, and the generic
  fallback handles anything truly unknown.
- **[Risk]** Wall-clock `prompt_ms` measurement during streaming reads
  the time-to-headers, not time-to-first-content-chunk. → Mitigation:
  Use `HttpCompletionOption.ResponseHeadersRead` (already in place);
  measure to first **content** byte read from the response stream, not
  to `SendAsync` completion. Document the precise measurement point in
  the extractor.
- **[Trade-off]** `BackendProbe` includes the raw JSON response strings
  (not deserialized DTOs) so strategies can read freely. Slight
  duplicate parsing across strategies; acceptable for the small
  payload sizes involved.

## Migration Plan

This is an in-process refactor — no external migration. Deployment
order:

1. Land the code change behind no feature flag (the behavior change is
   strictly better than current).
2. On daemon restart, capability cache rebuilds from the new chain.
3. Verify against live vLLM via `netclaw models --provider vllm-local`:
   expect `ContextWindow=256000`, `Input=Text|Image` for Qwen3.6-VL.
4. Verify against llama.cpp by repointing a provider and rerunning the
   same command: no regression in `meta.n_ctx_train` / `/props`
   detection.

Rollback: revert the PR. Capability cache is in-memory only; no
persistent state to undo.

## Open Questions

- Should we log the detected backend (vLLM / llama.cpp / generic) at
  resolver-info level so operators can see which strategy matched? Lean
  yes — useful for support diagnostics, free given we already detect it.
- Does `ModelIdNormalizer.GetCandidates` need any vLLM-specific
  candidate-generation logic? Open question; expect not, since vLLM's
  served model name is typically the literal HF id when admins
  configure `--served-model-name`. Will validate empirically against
  live deployment.

## Appendix A — Structural Composition

```
                       IModelCapabilityResolver
                       + string? ProviderType { get; }   // null = oracle, always runs
                       + Task<ResolvedModelCapabilities?> ResolveAsync(...)
                                 △
                                 │ implements
        ┌────────────────────────┼──────────────────────────┐
        │                        │                          │
        │                  CompositeCapabilityResolver      │
        │                  (NEW: field-merge, not short-circuit)
        │                  (NEW: filters by model.Provider) │
        │                  - timeout: 5s per resolver       │
        │                  - holds IReadOnlyList<IModelCapabilityResolver>
        │                        │ delegates to             │
        │   ┌────────────────────┼──────────────────────────┼──────────────┐
        │   │                    │                          │              │
        │   ▼                    ▼                          ▼              ▼
   OpenAiCodex            Ollama                  OpenAiCompatible    HuggingFace
   CapabilityResolver     CapabilityResolver      CapabilityResolver  CapabilityResolver
   ProviderType="openai"  ProviderType="ollama"   ProviderType=        ProviderType=null
                                                  "openai-compatible"  (oracle, runs for all)
                                                  │
                                                  │ (NEW internal)
                                                  ▼
                                          IOpenAiBackendStrategy
                                          (Matches / Parse)
                                                  △
                              ┌───────────────────┼───────────────────┐
                              ▼                   ▼                   ▼
                       VllmBackendStrategy  LlamaCppBackend   GenericOpenAi
                       - max_model_len      Strategy          BackendStrategy
                       - owned_by:"vllm"    - /props.n_ctx    - last-resort
                       - leaves modality    - meta.n_ctx_train  identity-only
                         null                - /props.modalities.vision

                                         shared probe:
                                  ┌─────────────────────────┐
                                  │      BackendProbe       │
                                  │ - modelsJson (string)   │
                                  │ - propsJson (string?)   │
                                  │ - modelId (string)      │
                                  └─────────────────────────┘


                       OpenAiCompatibleChatClient
                       (one chat completion request)
                                 │
                                 │ runs each on every response
                                 ▼
                          ITimingsExtractor
                          (Extract(JsonElement, UsageDetails))
                                 △
                  ┌──────────────┴──────────────┐
                  ▼                             ▼
        LlamaCppTimingsExtractor     VllmTimingsExtractor
        - timings.cache_n             - usage.prompt_tokens_details
        - timings.prompt_ms             .cached_tokens
        - timings.predicted_per_sec
                  │                             │
                  └─────────────┬───────────────┘
                                │ both write into same UsageDetails;
                                │ non-overlapping field paths
                                ▼
                     wall-clock prompt_ms fallback
                     (sets PromptMs iff still null)
```

## Appendix B — Execution Flow (vLLM-served Qwen3.6-VL)

```
SessionActor → ModelCapabilityActor.Ask(
                  GetModelCapabilities("Qwen/Qwen3.6-VL-30B-FP8"))
                          │ cache miss
                          ▼
              CompositeCapabilityResolver.ResolveAsync(modelId)
                  │
                  │ activeProvider = "openai-compatible"   (from ModelReference)
                  │ initialize merged = (modelId, null, null, null)
                  │
                  ├─[1]─► OpenAiCodexCapabilityResolver  (ProviderType="openai")
                  │         skipped — provider mismatch
                  │
                  ├─[2]─► OllamaCapabilityResolver       (ProviderType="ollama")
                  │         skipped — provider mismatch
                  │
                  ├─[3]─► OpenAiCompatibleCapabilityResolver
                  │         (ProviderType="openai-compatible")  → MATCH
                  │
                  │         GET /v1/models → 200 (max_model_len:256000, owned_by:"vllm")
                  │         GET /props      → 404
                  │         BackendProbe wraps both
                  │
                  │         VllmBackendStrategy.Matches → TRUE
                  │         VllmBackendStrategy.Parse   → (modelId,null,null,256000)
                  │
                  │         merge → (modelId, null, null, 256000)
                  │
                  ├─[4]─► OpenRouterOracleResolver       (ProviderType=null, oracle)
                  │         queries OpenRouter catalog → miss for Qwen3.6-VL
                  │         returns null → merged unchanged
                  │
                  └─[5]─► HuggingFaceCapabilityResolver  (ProviderType=null, oracle)
                            GET huggingface.co/api/models/Qwen/Qwen3.6-VL-30B-FP8
                            pipeline_tag = "image-text-to-text"
                            returns (modelId, Text|Image, Text, null)
                            merge → (modelId, Text|Image, Text, 256000)   ✓
                  │
                  ▼
              ModelCapabilityResolution.ResolveModelCapabilities(models, detected)
                  - manual override? no
                  - context-window override clamp? OK
                  - apply text-only default? not needed
                  │
                  ▼
                  ModelCapabilities {
                      ModelId             = "Qwen/Qwen3.6-VL-30B-FP8",
                      ContextWindowTokens = 256000,
                      InputModalities     = Text|Image,
                      OutputModalities    = Text
                  }
```

Two key invariants the diagram preserves:

- **Provider-native resolvers skip if `ProviderType` doesn't match the
  model's active provider.** Oracle resolvers (`ProviderType == null`)
  always run.
- **Composite walks the whole eligible chain** and merges first-non-null
  per field — `OpenAiCompatible` filling `ContextWindowTokens` does not
  prevent `HuggingFace` from filling modalities.

## Appendix C — Chat-client Timings Flow

```
OpenAiCompatibleChatClient.GetStreamingResponseAsync(...)
   │
   ├── t0 = TimeProvider.GetTimestamp()
   ├── HttpClient.SendAsync(..., HttpCompletionOption.ResponseHeadersRead)
   ├── first content byte read from response stream → t1
   │
   │   on final response JSON parse:
   ├── foreach extractor in [LlamaCpp, Vllm]:
   │      extractor.Extract(root, usageDetails)
   │      // LlamaCpp reads timings.* (no-op on vLLM)
   │      // Vllm reads usage.prompt_tokens_details.cached_tokens (no-op on llama.cpp)
   │
   └── if usageDetails.PromptMs is null:
          usageDetails.PromptMs = (t1 - t0).TotalMilliseconds
          // tagged as client-measured (includes network RTT)
```
