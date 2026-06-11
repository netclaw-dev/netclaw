# netclaw-model-capabilities Specification

## Purpose

Define model capability detection, caching, and modality representation for
multimodal LLM support.

## Requirements

### Requirement: Model modality type system

The system SHALL represent model capabilities using a `ModelModality` flags
enum with values `Text`, `Image`, `Audio`, and `Video`. Model capabilities
SHALL be expressed as a pair of `InputModalities` and `OutputModalities`
values, each being a combination of `ModelModality` flags.

#### Scenario: Modality flags compose

- **GIVEN** a model that accepts text and images and produces text
- **WHEN** its capabilities are represented
- **THEN** `InputModalities` SHALL equal `Text | Image`
- **AND** `OutputModalities` SHALL equal `Text`

#### Scenario: Default modality is text-only

- **GIVEN** a model whose capabilities cannot be determined
- **WHEN** the system assigns default capabilities
- **THEN** `InputModalities` SHALL equal `Text`
- **AND** `OutputModalities` SHALL equal `Text`

### Requirement: Singleton capability cache actor

The system SHALL maintain a singleton `ModelCapabilityActor` registered in the
`ActorRegistry`. The actor SHALL maintain an in-memory cache of model
capabilities keyed by model ID. Other actors SHALL query capabilities via
`Ask<ModelCapabilities>` using a `GetModelCapabilities` message containing the
model ID.

#### Scenario: First query triggers lookup

- **GIVEN** the capability cache has no entry for model `anthropic/claude-sonnet-4`
- **WHEN** an actor sends `GetModelCapabilities("anthropic/claude-sonnet-4")`
- **THEN** the actor SHALL resolve capabilities from the detection hierarchy
- **AND** cache the result
- **AND** respond with `ModelCapabilities` containing the resolved modalities

#### Scenario: Cached query returns immediately

- **GIVEN** the capability cache has an entry for model `qwen3:30b`
- **WHEN** an actor sends `GetModelCapabilities("qwen3:30b")`
- **THEN** the actor SHALL respond from cache without external API calls

#### Scenario: Concurrent queries for same model are deduplicated

- **GIVEN** a lookup for model `llava:latest` is in progress
- **WHEN** additional `GetModelCapabilities("llava:latest")` queries arrive
- **THEN** the actor SHALL stash duplicate queries
- **AND** respond to all stashed queries with the same result when the lookup completes

### Requirement: Provider-native capability detection

The system SHALL extract modality data from provider APIs that natively expose
it, as the highest-priority detection source.

#### Scenario: Ollama capability detection

- **GIVEN** a model is configured on an Ollama provider
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL call `POST /api/show` on the Ollama endpoint
- **AND** map the `capabilities` array to `ModelModality` flags
- **AND** `"vision"` in capabilities SHALL map to `InputModalities` including `Image`

#### Scenario: OpenRouter capability detection

- **GIVEN** a model is configured on an OpenRouter provider
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL use `architecture.input_modalities` and
  `architecture.output_modalities` from the OpenRouter model listing
- **AND** map array values (`"text"`, `"image"`, `"audio"`, `"video"`) to
  corresponding `ModelModality` flags

### Requirement: OpenRouter oracle for cross-provider lookup

The system SHALL use OpenRouter's public `GET /api/v1/models` endpoint as a
capability oracle for models accessed through providers that do not expose
capability metadata (Anthropic, OpenAI). This lookup SHALL NOT require an API
key.

#### Scenario: Anthropic model resolved via OpenRouter oracle

- **GIVEN** a model `claude-sonnet-4-20250514` is configured on the Anthropic provider
- **AND** the Anthropic API does not expose modality metadata
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL query OpenRouter's model listing
- **AND** normalize the model ID for matching (e.g., strip date suffix, add
  provider prefix)
- **AND** return capabilities from the matched OpenRouter entry

#### Scenario: OpenRouter oracle unreachable

- **GIVEN** the OpenRouter API is unreachable or times out within 5 seconds
- **WHEN** capabilities are resolved for a model requiring oracle lookup
- **THEN** the system SHALL fall through to the next detection tier
- **AND** log a warning indicating oracle lookup failed

### Requirement: HuggingFace fallback detection

The system SHALL fall back to querying the HuggingFace Hub API at
`GET https://huggingface.co/api/models/{id}` for models not found via
provider-native detection or the OpenRouter oracle.

#### Scenario: Open-source model resolved via HuggingFace

- **GIVEN** a model is not found in the OpenRouter catalog
- **AND** the model has a HuggingFace model ID
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL query the HuggingFace API
- **AND** map the `pipeline_tag` field to `ModelModality` flags
- **AND** `"image-text-to-text"` SHALL map to `InputModalities: Text | Image`

#### Scenario: HuggingFace lookup fails

- **GIVEN** the HuggingFace API returns a 404 or is unreachable
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL use the default text-only capabilities
- **AND** log a warning

### Requirement: Model ID normalization

The system SHALL normalize model IDs when performing cross-provider lookups to
account for provider-specific naming conventions.

#### Scenario: Date suffix stripped for matching

- **GIVEN** a model ID `claude-sonnet-4-20250514`
- **WHEN** performing an OpenRouter oracle lookup
- **THEN** the system SHALL attempt matching against `claude-sonnet-4`
  and `anthropic/claude-sonnet-4`

#### Scenario: Ollama tag stripped for matching

- **GIVEN** a model ID `llava:latest`
- **WHEN** performing a cross-provider lookup
- **THEN** the system SHALL attempt matching against `llava`

### Requirement: Manual capability override

The system SHALL allow operators to explicitly declare model capabilities in
the `ModelReference` configuration. Manual overrides SHALL take precedence
over all automated detection sources.

#### Scenario: Manual override bypasses detection

- **GIVEN** a `ModelReference` with explicitly configured `InputModalities`
  and `OutputModalities`
- **WHEN** capabilities are resolved for that model
- **THEN** the system SHALL use the configured values without querying any
  external API

### Requirement: Capability lookup failure resilience

The system SHALL NOT block session startup or message processing if capability
detection fails. All detection failures SHALL result in text-only defaults
with logged warnings.

#### Scenario: All detection tiers fail

- **WHEN** provider-native, OpenRouter oracle, and HuggingFace fallback all
  fail for a model
- **THEN** the system SHALL cache `InputModalities: Text, OutputModalities: Text`
  for that model ID
- **AND** log a warning with the model ID and failure reasons
- **AND** the session SHALL proceed normally with text-only behavior

### Requirement: Model capability resolution produces standalone ModelCapabilities

The model capability detection pipeline SHALL produce a `ModelCapabilities` record
as its output type. This record SHALL be registered as a standalone DI singleton,
independent of `SessionConfig`. The `SessionConfig` type SHALL NOT contain
`ModelId`, `ContextWindowTokens`, `InputModalities`, or `OutputModalities` —
these are exclusively owned by `ModelCapabilities`.

Consumers that previously read model properties from `SessionConfig` SHALL
instead take `ModelCapabilities` as a dependency.

#### Scenario: ModelCapabilities resolved and registered separately

- **GIVEN** the daemon startup resolves model capabilities via the detection
  hierarchy
- **WHEN** the resolved capabilities are registered in DI
- **THEN** `ModelCapabilities` is available as a separate singleton
- **AND** `SessionConfig` does not carry model-derived fields

#### Scenario: LlmSessionActor receives ModelCapabilities via DI

- **GIVEN** `ModelCapabilities` is registered in the DI container
- **WHEN** `resolver.Props<LlmSessionActor>(entityId)` resolves constructor params
- **THEN** the actor receives `ModelCapabilities` as a separate parameter
- **AND** uses `ModelCapabilities.ContextWindowTokens` for compaction decisions
- **AND** uses `ModelCapabilities.InputModalities` for modality gating

#### Scenario: DaemonRuntimeStatusService uses ModelCapabilities

- **GIVEN** `DaemonRuntimeStatusService` needs model display information
- **WHEN** the service is constructed
- **THEN** it takes `ModelCapabilities` as a dependency (not `SessionConfig`)
- **AND** reads `ModelId`, `InputModalities`, and `OutputModalities` from it
