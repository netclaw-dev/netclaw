## MODIFIED Requirements

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
