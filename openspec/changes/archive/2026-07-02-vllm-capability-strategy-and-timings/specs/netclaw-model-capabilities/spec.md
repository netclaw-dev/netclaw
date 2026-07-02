# netclaw-model-capabilities Delta: vLLM Strategy & Timings

## ADDED Requirements

### Requirement: OpenAI-compatible backend strategy selection

The system SHALL identify the concrete backend serving an OpenAI-compatible
endpoint (llama.cpp, vLLM, or unknown/generic) from a single probe of
`/v1/models` and `/props`, and dispatch capability parsing to the matching
backend strategy. Strategies SHALL be evaluated in priority order: vLLM,
llama.cpp, generic. The first strategy whose match predicate succeeds
SHALL produce the resolver's result.

#### Scenario: vLLM backend detected via max_model_len

- **GIVEN** an OpenAI-compatible endpoint where `GET /v1/models` returns a
  model entry containing a top-level `max_model_len` field
- **AND** `GET /props` returns HTTP 404
- **WHEN** capabilities are resolved
- **THEN** the vLLM strategy SHALL match the probe
- **AND** `ContextWindowTokens` SHALL equal the `max_model_len` value
- **AND** `InputModalities` and `OutputModalities` SHALL be left null for
  later resolvers to fill

#### Scenario: vLLM backend detected via owned_by

- **GIVEN** an OpenAI-compatible endpoint where `GET /v1/models` returns a
  model entry with `owned_by: "vllm"`
- **WHEN** capabilities are resolved
- **THEN** the vLLM strategy SHALL match the probe regardless of which
  numeric fields are present

#### Scenario: llama.cpp backend detected via /props

- **GIVEN** an OpenAI-compatible endpoint where `GET /props` returns
  HTTP 200 with a `default_generation_settings.params.n_ctx` value
- **WHEN** capabilities are resolved
- **THEN** the llama.cpp strategy SHALL match the probe
- **AND** `ContextWindowTokens` SHALL prefer the `/props` value over
  `meta.n_ctx_train` from `/v1/models`
- **AND** `InputModalities` SHALL include `Image` when
  `/props.modalities.vision` is `true`

#### Scenario: Generic OpenAI-compatible fallback

- **GIVEN** an OpenAI-compatible endpoint that exposes neither vLLM nor
  llama.cpp signals
- **WHEN** capabilities are resolved
- **THEN** the generic strategy SHALL match
- **AND** the resolver SHALL return `(modelId, null, null, null)` so the
  downstream chain (HuggingFace, OpenRouter oracle) fills in fields

### Requirement: Provider-aware resolver scoping

Every `IModelCapabilityResolver` SHALL expose a nullable
`ProviderType` property identifying which provider type the resolver
speaks for. Provider-native resolvers SHALL return a non-null value
(e.g., `"openai"`, `"ollama"`, `"openai-compatible"`). Cross-provider
oracle resolvers (`OpenRouterOracleResolver`,
`HuggingFaceCapabilityResolver`) SHALL return `null`, indicating they
apply to all providers.

The composite capability resolver SHALL invoke a resolver only when its
`ProviderType` is `null` OR when its `ProviderType` matches the active
model's `ModelReference.Provider`. Filtering SHALL happen before the
merge walk so foreign provider-native resolvers never issue HTTP probes
for models they cannot resolve.

#### Scenario: Foreign provider-native resolver skipped

- **GIVEN** a model with `ModelReference.Provider = "openai-compatible"`
- **AND** an `OllamaCapabilityResolver` (`ProviderType = "ollama"`)
  registered in the chain
- **WHEN** capabilities are resolved
- **THEN** the Ollama resolver SHALL NOT be invoked
- **AND** no `POST /api/show` request SHALL be issued to the Ollama
  endpoint

#### Scenario: Matching provider-native resolver runs

- **GIVEN** a model with `ModelReference.Provider = "openai-compatible"`
- **AND** an `OpenAiCompatibleCapabilityResolver`
  (`ProviderType = "openai-compatible"`) registered in the chain
- **WHEN** capabilities are resolved
- **THEN** the OpenAI-compatible resolver SHALL be invoked

#### Scenario: Oracle resolvers always eligible

- **GIVEN** a model on any provider
- **AND** `HuggingFaceCapabilityResolver` (`ProviderType = null`)
  registered in the chain
- **WHEN** capabilities are resolved
- **THEN** the HuggingFace resolver SHALL be eligible regardless of the
  model's provider

#### Scenario: Parallel native backends do not cross-probe

- **GIVEN** both an `OllamaCapabilityResolver` and an
  `OpenAiCompatibleCapabilityResolver` are registered
- **AND** the active query targets a model on the OpenAI-compatible
  provider
- **WHEN** the composite walks the chain
- **THEN** only the OpenAI-compatible resolver issues HTTP probes
- **AND** the Ollama endpoint receives zero traffic for the query

### Requirement: Composite resolver field-merge semantics

The composite capability resolver SHALL combine results across registered
resolvers by taking the first non-null value per field rather than
short-circuiting on the first non-null result. A resolver SHALL be
permitted to populate any subset of `(InputModalities, OutputModalities,
ContextWindowTokens)`, returning null for fields it cannot determine.

Per-resolver timeout (5 seconds) and exception-to-warning behavior from
the existing resolver chain SHALL be preserved.

#### Scenario: vLLM context + HuggingFace modality merge

- **GIVEN** the OpenAI-compatible resolver returns `(modelId, null, null, 256000)`
- **AND** the HuggingFace resolver returns `(modelId, Text|Image, Text, null)`
- **WHEN** the composite resolver merges these results
- **THEN** the merged record SHALL be
  `(modelId, Text|Image, Text, 256000)`

#### Scenario: First non-null wins per field

- **GIVEN** resolver A returns `ContextWindowTokens = 200000`
- **AND** resolver B (later in the chain) returns `ContextWindowTokens = 256000`
- **WHEN** the composite resolver merges these results
- **THEN** the merged record SHALL preserve the value from resolver A

#### Scenario: All resolvers return null fields

- **GIVEN** every registered resolver returns null for `InputModalities`,
  `OutputModalities`, and `ContextWindowTokens`
- **WHEN** the composite returns its merged result
- **THEN** the merged record SHALL carry null values
- **AND** downstream consumption SHALL apply text-only / default-context
  defaults at the boundary (existing
  `ModelCapabilityResolution.ResolveModelCapabilities` behavior)

### Requirement: Per-request timings extraction across backends

The OpenAI-compatible chat client SHALL extract per-request token usage
and timing telemetry from the response using a chain of
`ITimingsExtractor` implementations. Extractors SHALL run in sequence on
every response. Field paths are non-overlapping across supported
backends so multiple extractors can safely run without conflict.

`UsageDetails.CachedInputTokens` SHALL be populated from whichever
backend-specific field is present:
- llama.cpp: `timings.cache_n`
- vLLM (and any OpenAI-standard prefix-cache backend):
  `usage.prompt_tokens_details.cached_tokens`

`UsageDetails.PromptMs` SHALL prefer a server-supplied value when
present (llama.cpp `timings.prompt_ms`). When no server value is
available, the chat client SHALL populate `PromptMs` from a client-side
wall-clock measurement between HTTP request send and the first response
byte received.

#### Scenario: vLLM cached_tokens flow through

- **GIVEN** a vLLM `/v1/chat/completions` response with
  `usage.prompt_tokens_details.cached_tokens = 1024`
- **WHEN** the chat client parses usage
- **THEN** `UsageDetails.CachedInputTokens` SHALL equal `1024`

#### Scenario: llama.cpp timings still extracted

- **GIVEN** a llama.cpp `/v1/chat/completions` response with a top-level
  `timings` object containing `cache_n = 2048` and `prompt_ms = 450.0`
- **WHEN** the chat client parses usage
- **THEN** `UsageDetails.CachedInputTokens` SHALL equal `2048`
- **AND** `UsageDetails.PromptMs` SHALL equal `450.0`

#### Scenario: Fallback wall-clock prompt_ms

- **GIVEN** a backend response that contains no `timings.prompt_ms` and no
  other server-provided prompt latency
- **WHEN** the chat client receives the first response byte 120ms after
  sending the request
- **THEN** `UsageDetails.PromptMs` SHALL equal approximately `120.0`
  (within wall-clock measurement tolerance)

## MODIFIED Requirements

### Requirement: HuggingFace fallback detection

The system SHALL query the HuggingFace Hub API at
`GET https://huggingface.co/api/models/{id}` to fill capability fields
that earlier resolvers in the chain left unpopulated, in addition to the
existing role as a fallback when provider-native and OpenRouter oracle
lookups produce no result. HuggingFace lookup SHALL be skipped for model
IDs that do not match the `<org>/<model>` form (existing
`ModelIdNormalizer.GetCandidates` filter).

#### Scenario: Open-source model resolved via HuggingFace

- **GIVEN** a model is not found in the OpenRouter catalog
- **AND** the model has a HuggingFace model ID
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL query the HuggingFace API
- **AND** map the `pipeline_tag` field to `ModelModality` flags
- **AND** `"image-text-to-text"` SHALL map to `InputModalities: Text | Image`

#### Scenario: HuggingFace fills modality left null by OpenAI-compatible resolver

- **GIVEN** the OpenAI-compatible resolver returned a non-null
  `ContextWindowTokens` but null `InputModalities` (vLLM backend)
- **AND** the served model id has the `<org>/<model>` form
- **WHEN** the composite continues through the resolver chain
- **THEN** the HuggingFace resolver SHALL be consulted
- **AND** its returned `InputModalities` / `OutputModalities` SHALL merge
  into the final result

#### Scenario: HuggingFace lookup fails

- **GIVEN** the HuggingFace API returns a 404 or is unreachable
- **WHEN** capabilities are resolved for that model
- **THEN** the resolver SHALL return null modality fields
- **AND** downstream defaulting SHALL apply text-only at the consumption
  boundary
- **AND** a warning SHALL be logged

### Requirement: Capability lookup failure resilience

The system SHALL NOT block session startup or message processing if
capability detection fails. Resolvers in the chain MAY return null values
for any field they cannot determine; the composite SHALL propagate those
nulls. The final consumption boundary
(`ModelCapabilityResolution.ResolveModelCapabilities`) SHALL apply
text-only modality and default-context defaults when no resolver supplies
a value, and SHALL log a warning identifying the unresolved fields.

#### Scenario: All detection tiers return null modality

- **WHEN** provider-native, OpenRouter oracle, OpenAI-compatible, and
  HuggingFace resolvers all return null for `InputModalities`
- **THEN** the consumption boundary SHALL apply
  `InputModalities: Text, OutputModalities: Text` for that model ID
- **AND** log a warning with the model ID and the unresolved fields
- **AND** the session SHALL proceed normally with text-only behavior

#### Scenario: Manual override still wins

- **GIVEN** a `ModelReference` with explicitly configured
  `InputModalities`
- **WHEN** capabilities are resolved
- **THEN** the configured value SHALL be used regardless of what any
  resolver returned (existing `ModelCapabilityResolution` precedence)
