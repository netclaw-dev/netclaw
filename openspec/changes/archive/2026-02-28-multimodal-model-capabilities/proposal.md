## Why

Netclaw currently treats all model input as text-only. The channel edge
(`ChannelInput`) already accepts `IReadOnlyList<AIContent>`, but
`ChannelPipeline.MapToCommand` strips everything except `TextContent` and
`SendUserMessage.Content` is a plain string. Before we can pass images, audio,
or other content types through the pipeline, we need to know what the
configured model actually supports. Without capability detection, we'd either
silently drop non-text content or send unsupported content types and get
provider errors at runtime.

This is the foundation for multimodal support — detection first, then plumbing.

## What Changes

- Introduce a `ModelModality` flags enum (`Text`, `Image`, `Audio`, `Video`)
  representing input and output modalities a model may support.
- Add `InputModalities` and `OutputModalities` fields to `DiscoveredModel` so
  capability metadata is captured during model discovery.
- Create a singleton `ModelCapabilityActor` that maintains an in-memory cache
  of model capabilities. Other actors query it to determine what a model
  accepts. The cache refreshes only when a previously-unseen model ID is
  encountered (model capabilities don't change — new models are published
  instead).
- Enrich `ProviderProbe` to extract modality data from provider-native sources:
  - **Ollama**: `/api/show` returns a `capabilities` array (e.g.,
    `["completion", "vision"]`).
  - **OpenRouter**: `/api/v1/models` returns `architecture.input_modalities`
    and `architecture.output_modalities` arrays.
- Use **OpenRouter as a capability oracle** for models accessed through any
  provider. A model's capabilities are intrinsic — Claude supports vision
  whether accessed via Anthropic, OpenRouter, or GitHub Copilot. The
  OpenRouter model listing is public (no API key required for reads) and
  covers most models across all major providers.
- Add **HuggingFace Hub API** as a fallback for open-source models not listed
  on OpenRouter, using `pipeline_tag` (e.g., `image-text-to-text`) and model
  metadata from `/api/models/{id}`.
- Surface resolved capabilities in `SessionConfig` so the session actor knows
  what content types it can forward to the LLM.

## Capabilities

### New Capabilities

- `netclaw-model-capabilities`: Model capability detection, caching, and
  querying. Covers the `ModelModality` type, the `ModelCapabilityActor`
  singleton, provider-native detection (Ollama, OpenRouter), cross-provider
  oracle lookup (OpenRouter public API), HuggingFace fallback, and manual
  config override.

### Modified Capabilities

- `netclaw-model-providers`: Enrich `DiscoveredModel` with modality fields.
  Extend `ProviderProbe` to extract capability metadata during model
  discovery. Add modality awareness to `SessionConfig`.

## Impact

- **`Netclaw.Configuration`**: New `ModelModality` flags enum. New fields on
  `DiscoveredModel` (`InputModalities`, `OutputModalities`). New optional
  modality override fields on `ModelReference` for manual config. New fields
  on `SessionConfig` to surface resolved capabilities to the session actor.
- **`Netclaw.Actors`**: New `ModelCapabilityActor` — a singleton (one per
  `ActorSystem`) that owns the in-memory capability cache. Responds to
  `GetModelCapabilities` queries. Populated lazily on first query per model
  ID, then cached indefinitely.
- **`Netclaw.Daemon`**: `ProviderProbe` gains modality extraction logic for
  Ollama `/api/show` and OpenRouter `/api/v1/models`. New HTTP client
  integration for OpenRouter oracle and HuggingFace fallback lookups.
- **`Netclaw.Cli`**: Model listing commands can optionally display supported
  modalities.
- **External dependencies**: HTTP calls to OpenRouter `/api/v1/models`
  (public, no auth) and HuggingFace `/api/models/{id}` (public, no auth) for
  cross-provider capability lookup. Both are read-only and cacheable.
- **No breaking changes**: All new fields have sensible defaults
  (`ModelModality.Text`). Existing configurations continue to work unchanged.

### Out of Scope

- Changing `SendUserMessage.Content` from `string` to `AIContent[]` (follow-up
  change: multimodal message plumbing).
- Persisting non-text content in the Protobuf journal (follow-up).
- Slack image/file extraction and forwarding (follow-up).
- Image/audio preprocessing or resizing (follow-up).

### PRD References

- PRD-005 (MP-002, MP-003): Provider abstraction and multi-provider support —
  this change enriches the provider metadata layer.
- PRD-009 (INPUT-003): Transport agnosticism — capability detection enables
  adapters to make informed decisions about content forwarding.
