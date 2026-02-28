## 1. Core Types

- [x] 1.1 Add `ModelModality` flags enum (`None`, `Text`, `Image`, `Audio`, `Video`) to `Netclaw.Configuration`
- [x] 1.2 Add `InputModalities` and `OutputModalities` properties to `DiscoveredModel` (default `ModelModality.Text`)
- [x] 1.3 Add optional `InputModalities` and `OutputModalities` override properties to `ModelReference` for manual config
- [x] 1.4 Add `InputModalities` and `OutputModalities` properties to `SessionConfig` (default `ModelModality.Text`)

## 2. Capability Resolution

- [x] 2.1 Define `IModelCapabilityResolver` interface with `Task<ResolvedModelCapabilities?> ResolveAsync(string modelId, CancellationToken ct)` in `Netclaw.Configuration`
- [x] 2.2 Implement `OpenRouterOracleResolver` — calls `GET /api/v1/models` (public, no auth), caches full model list, matches by normalized model ID
- [x] 2.3 Implement `HuggingFaceCapabilityResolver` — calls `GET https://huggingface.co/api/models/{id}`, maps `pipeline_tag` to `ModelModality` flags
- [x] 2.4 Implement model ID normalization logic (strip date suffixes, add provider prefixes, strip Ollama `:latest` tags)
- [x] 2.5 Implement `CompositeCapabilityResolver` that chains resolvers in priority order: OpenRouter oracle → HuggingFace fallback → text-only default

## 3. Capability Cache Actor

- [x] 3.1 Define protocol messages: `GetModelCapabilities`, `ModelCapabilitiesResponse`, internal `CapabilityResolved`
- [x] 3.2 Implement `ModelCapabilityActor` — singleton actor with `Dictionary<string, ModelCapabilitiesResponse>` cache, lazy lookup, waiting list for in-flight deduplication
- [x] 3.3 Add `ModelCapabilityActorKey` marker type to `ActorRegistryKeys.cs`
- [x] 3.4 Register `ModelCapabilityActor` in `NetclawAkkaHostingExtensions.StartActors` alongside session manager
- [x] 3.5 Register `IModelCapabilityResolver` chain and `HttpClient` dependencies in DI

## 4. Wiring

- [x] 4.1 Pass resolved capabilities through to `SessionConfig` construction

## 5. Testing

- [x] 5.1 Unit test `OpenRouterOracleResolver` with sample model listing JSON (multimodal model, text-only model, model not found)
- [x] 5.2 Unit test `HuggingFaceCapabilityResolver` with sample model metadata JSON (image-text-to-text, text-generation, 404)
- [x] 5.3 Unit test model ID normalization (date suffix stripping, provider prefix mapping, Ollama tag stripping)
- [x] 5.4 Unit test `CompositeCapabilityResolver` fallback chain (oracle succeeds, HuggingFace fallback, all fail → default)
- [x] 5.5 Actor test `ModelCapabilityActor` — first query triggers lookup, second query returns from cache, concurrent queries are deduplicated

## 6. Schema and Doc Updates

- [x] 6.1 Update JSON config schema with `ModelReference` modality override fields
- [x] 6.2 Update `docs/spec/configuration.md` with new modality fields
