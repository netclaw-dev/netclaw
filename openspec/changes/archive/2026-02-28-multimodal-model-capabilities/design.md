## Context

Netclaw's model layer currently tracks model identity (ID, provider, context
window) but has no concept of what content types a model accepts or produces.
The `DiscoveredModel` record has fields for cost and parameter count but nothing
about modality. The `ProviderProbe` extracts only `ModelId` from provider API
responses, discarding richer metadata that providers already return.

Meanwhile, the channel edge (`ChannelInput`) already accepts
`IReadOnlyList<AIContent>`, which is MEAI's multimodal content abstraction.
The pipeline strips non-text content today. Before we can stop stripping it,
the system needs to know what the configured model actually supports.

The singleton actor pattern is already established: `SessionManagerActorKey` is
registered in `ActorRegistry` via `StartActors` and resolved via
`IRequiredActor<T>` or `ActorRegistry.Get<T>()`.

## Goals / Non-Goals

**Goals:**

- Determine input/output modalities for any configured model at runtime.
- Cache capability lookups in-memory so repeated queries are free.
- Support all four provider types (Ollama, OpenRouter, Anthropic, OpenAI) with
  a single query interface.
- Allow manual override in configuration for models not discoverable through
  any automated source.

**Non-Goals:**

- Forwarding non-text content through the session pipeline (follow-up change).
- Persistence of capability data across restarts (in-memory cache rebuilds
  lazily; capability data is cheap to re-fetch).
- Real-time capability change detection (models don't change capabilities).
- Provider-specific behavior in the session actor based on capabilities (the
  session actor gets flags, not provider details).

## Decisions

### D1: Singleton `ModelCapabilityActor` with in-memory `Dictionary` cache

**Decision**: A single actor per `ActorSystem` owns the capability cache. Other
actors query it via `Ask<ModelCapabilities>`. The cache is a plain
`Dictionary<string, ModelCapabilities>` keyed by model ID.

**Rationale**: Actors are the natural concurrency boundary in this codebase. A
singleton actor avoids concurrent HTTP calls for the same model during startup
(multiple sessions starting simultaneously). The `ActorRegistry` pattern is
already proven with `SessionManagerActorKey`.

**Alternatives considered**:
- **DI singleton service with `ConcurrentDictionary`**: Simpler, but the
  capability lookup involves async HTTP calls to external APIs. An actor
  naturally serializes these and can stash duplicate queries while a lookup is
  in-flight. A service would need manual deduplication with `SemaphoreSlim` or
  `Lazy<Task<T>>`.
- **Static cache populated at startup**: Would delay startup while fetching
  capabilities for all configured models. Lazy per-model lookup is better —
  most sessions use the same model, so one lookup serves all.

### D2: Three-tier detection hierarchy

**Decision**: When a model's capabilities are not yet cached, resolve them in
this order:

1. **Provider-native** — If the model's provider supports capability metadata
   natively, use it:
   - Ollama: `POST /api/show` → `capabilities` array (e.g., `["completion",
     "vision"]`)
   - OpenRouter: already fetched during model listing — `architecture.input_modalities` and `architecture.output_modalities` arrays
2. **OpenRouter oracle** — For models on providers that don't expose
   capabilities (Anthropic, OpenAI, GitHub), query OpenRouter's public
   `GET /api/v1/models` endpoint. Match by model ID (with normalization — see
   D4). No API key required.
3. **HuggingFace fallback** — For open-source models not in OpenRouter's
   catalog, query `GET https://huggingface.co/api/models/{id}` and map
   `pipeline_tag` to modalities (e.g., `image-text-to-text` →
   `InputModalities: Text | Image, OutputModalities: Text`).
4. **Default** — If all lookups fail or are unavailable, default to
   `InputModalities: Text, OutputModalities: Text`. Log a warning so the
   operator knows capability detection failed for that model.

**Rationale**: Provider-native data is most authoritative and cheapest (local
for Ollama, already-fetched for OpenRouter). OpenRouter covers ~400+ models
across all major providers. HuggingFace covers the long tail of open-source
models. The default ensures the system never blocks on capability detection.

**Alternatives considered**:
- **HuggingFace only**: Coverage is excellent for open-source models but
  inconsistent for proprietary models (Claude, GPT). OpenRouter's structured
  `input_modalities`/`output_modalities` arrays are more reliable than
  parsing HuggingFace's `pipeline_tag` taxonomy.
- **Static table only**: Goes stale, doesn't work for custom/fine-tuned
  models, requires releases to add new models.

### D3: `ModelModality` as a `[Flags]` enum

**Decision**: Use a `[Flags]` enum for modality representation:

```csharp
[Flags]
public enum ModelModality
{
    None  = 0,
    Text  = 1 << 0,
    Image = 1 << 1,
    Audio = 1 << 2,
    Video = 1 << 3,
}
```

Capability records carry both `InputModalities` and `OutputModalities` as
separate `ModelModality` values.

**Rationale**: Flags compose naturally (`Text | Image`), are cheap to test
(`modalities.HasFlag(ModelModality.Image)`), serialize trivially as integers,
and are extensible (add `Document = 1 << 4` later without breaking existing
values).

**Alternatives considered**:
- **`HashSet<string>`**: More flexible but loses compile-time safety and
  requires string comparisons. The modality space is small and well-known.
- **Separate `bool` fields**: `SupportsVision`, `SupportsAudio`, etc. Doesn't
  compose well and requires new fields for each modality.

### D4: Model ID normalization for cross-provider lookup

**Decision**: When looking up a model via the OpenRouter oracle, normalize the
model ID to handle provider-specific suffixes and prefixes:

- Strip date suffixes: `claude-sonnet-4-20250514` → `claude-sonnet-4`
- Map known prefixes: bare `claude-sonnet-4` → `anthropic/claude-sonnet-4`
- Ollama tag stripping: `llava:latest` → `llava`

Maintain a small normalization table for known provider ID formats. If
normalization fails to find a match, fall through to the next tier.

**Rationale**: The same model has different IDs across providers. Anthropic uses
`claude-sonnet-4-20250514`, OpenRouter uses `anthropic/claude-sonnet-4`.
Without normalization, cross-provider lookup would miss matches.

### D5: Cache populated lazily, never evicted

**Decision**: The cache starts empty. On the first `GetModelCapabilities` query
for a given model ID, the actor fetches capabilities, caches them, and
responds. Subsequent queries for the same model ID return immediately from
cache. Entries are never evicted or refreshed.

**Rationale**: Model capabilities are immutable — providers publish new model
IDs rather than changing capabilities of existing ones. The cache only grows
when a new model ID is queried, which happens rarely (model changes are
infrequent operator actions). Memory footprint is negligible (a few hundred
bytes per model entry, and operators use a handful of models).

### D6: Capability resolution service behind the actor

**Decision**: Extract the HTTP lookup logic into an `IModelCapabilityResolver`
service injected via DI. The actor delegates to this service for actual HTTP
calls. This keeps the actor focused on caching/deduplication and makes the
resolution logic independently testable.

```
ModelCapabilityActor (cache + dedup)
  └── IModelCapabilityResolver (HTTP lookups)
        ├── OllamaCapabilityResolver
        ├── OpenRouterOracleResolver
        └── HuggingFaceCapabilityResolver
```

**Rationale**: Actor code should be thin. The HTTP parsing logic (mapping
Ollama's `capabilities` array, OpenRouter's `input_modalities`, HuggingFace's
`pipeline_tag`) is pure mapping logic that benefits from direct unit testing
without actor infrastructure.

## Risks / Trade-offs

**[Risk] OpenRouter API availability** → The oracle lookup is a nice-to-have,
not a hard dependency. If OpenRouter is unreachable, the system falls through
to HuggingFace and then to `Text`-only default. A 5-second timeout prevents
blocking. The actor logs the failure and caches the default so it doesn't
retry on every query.

**[Risk] Model ID normalization misses** → Normalization is heuristic-based.
Custom fine-tuned models or obscure providers may not match. Mitigation:
manual override in `ModelReference` config always wins. Log when normalization
fails so operators can add overrides.

**[Risk] Stale cache after model switch** → If the operator changes the
configured model (via the parallel model-switching branch), the new model ID
will trigger a fresh lookup on first query. No cache invalidation needed —
new ID = new cache entry.

**[Risk] HuggingFace pipeline_tag taxonomy changes** → The mapping from
`pipeline_tag` values to `ModelModality` flags is a maintained lookup table.
If HuggingFace adds new tags, we add new mappings. Unmapped tags fall through
to default.

**[Trade-off] No persistent cache** → Capability data is re-fetched after
process restart. This is acceptable because: (a) most operators use 1-3
models, so it's 1-3 HTTP calls; (b) the data is immutable so there's no
stale-cache risk; (c) avoiding persistence keeps the implementation simple.

## Actor Boundary and Failure Modes

**Actor lifecycle**: The `ModelCapabilityActor` is started in `StartActors`
alongside `SessionManagerActorKey`. It is a plain actor (no persistence, no
stash) with a `Dictionary<string, ModelCapabilities>` as state.

**Message protocol**:
- `GetModelCapabilities(string ModelId)` → query
- `ModelCapabilities(string ModelId, ModelModality InputModalities, ModelModality OutputModalities)` → response
- Internal: `CapabilityResolved(string ModelId, ModelCapabilities Result)` for
  async HTTP completion piping back to the actor

**In-flight deduplication**: If multiple queries arrive for the same model ID
while a lookup is in progress, the actor stashes duplicates and unstashes them
when the result arrives — all get the same cached answer.

**Failure recovery**: If the resolver throws (network error, parse error), the
actor caches `ModelModality.Text` as default for that model ID and logs a
warning. It does not retry — the operator can restart the process or add a
manual override if needed. This prevents retry storms against unreachable
APIs.

**Supervision**: Default supervision strategy (restart on unhandled exception).
The dictionary cache is lost on restart, which is fine — it rebuilds lazily.
