## Why

`LlmSessionActor` is 3,100+ lines with 19 constructor parameters mixing 10+ concerns
(state machine, LLM invocation, tool execution, compaction, memory pipeline, title
generation, subscriber management, delivery retry, slash commands, context injection,
trust derivation, processing watchdog). Adding a `Passivating` state for graceful
drain (#326) requires duplicating handler registration or brittle boolean flags because
the implicit `Become()` state machine has no validation layer. `SessionConfig` (#414)
conflates 16+ properties across 4 distinct concerns, making it unclear what operators
should actually configure. This blocks the planned Akka.Agents framework extraction —
the actor needs clean seams before it can become a reusable backbone.

Ref: PRD-001 (MVP session lifecycle), GitHub #411, #414, #326

## What Changes

- **BREAKING**: `SessionConfig` split into 3 types: slimmed `SessionConfig` (user-facing),
  `ModelCapabilities` (runtime-derived), `SessionTuning` (internal constants). All consumers
  updated in one pass (clean break, no deprecation period).
- **BREAKING**: `LlmSessionActor` constructor signature changes from 19 individual params to
  ~6 composite dependency records (`SessionServices`, `SessionToolServices`,
  `SessionMemoryServices`, `SessionObservability`, `ModelCapabilities`, `SessionConfig`).
- Explicit `SessionPhase` enum (`Recovering`, `Ready`, `Processing`, `Compacting`,
  `Passivating`) with validated `TransitionTo()` replacing raw `Become()` calls.
- `Passivating` behavior added: buffers messages, requests final memory distillation from
  observer, saves snapshot, stops self.
- 5 handler modules extracted: `SessionSubscriberManager`, `DeliveryRetryHandler`,
  `TurnStateTracker`, `DiscoveredToolCache`, `ProcessingWatchdog`.
- 5 static pipeline utilities extracted: `SessionTitleGenerator`, `SessionCompactionPipeline`,
  `SessionLlmInvoker`, `SessionToolExecutionPipeline`, `SessionRecallManager`.
- JSON schema for Session section updated with explicit properties and
  `additionalProperties: false`.
- Feature flags (`MemorySidecarsEnabled`, `DeterministicRetrievalEnabled`) moved to
  `SessionTuning` for eventual removal.
- No persistence event or snapshot changes. Wire format is untouched.

## Capabilities

### New Capabilities

- `session-state-machine`: Explicit session phase lifecycle (enum, validated transitions,
  Passivating state) extracted from the implicit Become()-based state machine. Covers
  phase transition rules, lifecycle hooks, and the Passivating drain protocol.
- `session-config-decomposition`: SessionConfig split into ModelCapabilities (runtime-derived),
  SessionTuning (internal constants), and slimmed SessionConfig (user-facing operational
  settings). Covers type definitions, DI wiring, JSON schema, and migration.

### Modified Capabilities

- `netclaw-session`: State machine is formalized with explicit phase enum. Passivating
  behavior is a new session lifecycle state. Constructor dependency signature changes.
  Handler modules and pipeline extractions restructure internal implementation while
  preserving all existing behavioral requirements.
- `netclaw-model-capabilities`: `ModelCapabilities` record is extracted from `SessionConfig`
  as a standalone DI-registered type. The capability resolution pipeline produces
  `ModelCapabilities` directly instead of overlaying fields onto `SessionConfig`.

## Impact

- **Code**: `LlmSessionActor.cs` reduced from ~3,100 to ~800-1,000 lines. ~10 new files
  created under `src/Netclaw.Actors/Sessions/Handlers/` and
  `src/Netclaw.Actors/Sessions/Pipelines/`.
- **Configuration**: `SessionConfig` loses model-derived and internal-tuning properties.
  `netclaw.json` Session section gains `Tuning` nested object. Schema enforces
  `additionalProperties: false`.
- **DI**: `ModelCapabilities` registered as separate singleton. Composite service records
  registered for actor resolution.
- **Tests**: ~20 test files need `new SessionConfig { ... }` updates. Extracted modules
  gain unit tests independent of ActorSystem.
- **Persistence**: No changes. All 4 event types and snapshot format untouched.
- **Security**: No ACL or policy changes. Trust context derivation relocates but behavior
  is identical.
- **Downstream**: Akka.Agents extraction becomes mechanical after this refactoring — modules
  with clean interfaces can be moved to a separate package.
