## 1. SessionConfig Decomposition (#414)

- [x] 1.1 Create `ModelCapabilities` record in `src/Netclaw.Configuration/ModelCapabilities.cs` with `ModelId`, `ContextWindowTokens`, `InputModalities`, `OutputModalities`, `CompactionModelId`, and `CompactionTokenLimit(double)` method
- [x] 1.2 Create `SessionTuning` record in `src/Netclaw.Configuration/SessionTuning.cs` with all internal constants and feature flags, matching current production defaults
- [x] 1.3 Slim `SessionConfig` — remove model-derived properties, remove internal tuning flat properties, add `SessionTuning Tuning` nested property, convert timeout properties from `int` seconds to `TimeSpan`
- [x] 1.4 Add `SessionConfig.BindFromConfiguration(IConfigurationSection)` static factory that converts raw int-seconds to `TimeSpan` with minimum 1s enforcement
- [x] 1.5 Update `ModelCapabilityResolution` to produce `ModelCapabilities` directly instead of overlaying fields onto `SessionConfig`
- [x] 1.6 Update `src/Netclaw.Daemon/Program.cs` — register `ModelCapabilities` as separate singleton, use `SessionConfig.BindFromConfiguration()` for session config
- [x] 1.7 Update `src/Netclaw.Cli/Program.cs` — same DI changes as daemon + ChatViewModel migration
- [x] 1.8 Update `DaemonRuntimeStatusService` to take `ModelCapabilities` instead of reading model fields from `SessionConfig`
- [x] 1.9 Update `SQLiteMemoryRecallCoordinator` to take `SessionTuning` for feature flags and `SessionConfig` for sidecar timeout
- [x] 1.10 Update `LlmSessionActor` constructor to accept `ModelCapabilities` as separate parameter; migrate all `_config.ModelId` → `_model.ModelId`, `_config.ContextWindowTokens` → `_model.ContextWindowTokens`, etc.
- [x] 1.11 Replace all `TimeSpan.FromSeconds(Math.Max(1, _config.XxxTimeoutSeconds))` with direct `_config.XxxTimeout` property access throughout `LlmSessionActor`
- [x] 1.12 Replace all `_config.KeepRecentToolResults` etc. with `_config.Tuning.Xxx` throughout `LlmSessionActor`
- [x] 1.13 Update `netclaw-config.v1.schema.json` Session section — explicit properties, nested `Tuning` object, `additionalProperties: false`
- [x] 1.14 Update all test files constructing `new SessionConfig { ... }` to use new type structure (~15 files)
- [x] 1.15 Verify: `dotnet build` passes (0 errors), `dotnet test` passes (1,400 tests, 0 failures)

## 2. Constructor Dependency Reduction

- [x] 2.1 Create composite record types in `src/Netclaw.Actors/Sessions/SessionDependencies.cs`: `SessionServices`, `SessionToolServices`, `SessionMemoryServices`, `SessionObservability`
- [x] 2.2 Refactor `LlmSessionActor` constructor from 19 params to 7 (`entityId`, `ModelCapabilities`, `SessionConfig`, `SessionServices`, `SessionToolServices?`, `SessionMemoryServices?`, `SessionObservability?`)
- [x] 2.3 Update `src/Netclaw.Daemon/Program.cs` — register composite records as DI singletons before Akka setup
- [x] 2.4 Update all 9 test files to register composite records for DI resolution
- [x] 2.5 Verify: `dotnet build` passes (0 errors), `dotnet test` passes (1,400 tests, 0 failures)

## 3. State Machine Formalization (#411)

- [x] 3.1 Create `SessionPhase` enum in `src/Netclaw.Actors/Sessions/SessionPhase.cs` with `Recovering`, `Ready`, `Processing`, `Compacting`, `Passivating`
- [x] 3.2 Add `_currentPhase` field and `TransitionTo(SessionPhase)` method to `LlmSessionActor` with legal transition validation and `InvalidOperationException` for illegal transitions
- [x] 3.3 Replace all `Become(Ready)` / `Become(Processing)` / `Become(Compacting)` calls with `TransitionTo(SessionPhase.Xxx)` (11 replacements)
- [x] 3.4 Add `SessionPhaseChanged`, `RequestFinalDistillation`, `PassivationTimeout` message types in LlmMessages.cs
- [x] 3.5 Add phase transition logging (`session_phase_transition from=X to=Y`) in TransitionTo()
- [x] 3.6 Implement `Passivating` behavior: buffer messages, send `DistillMemories` to observer, 5s grace period timer, snapshot + stop
- [x] 3.7 Refactor `ReceiveTimeout` handler in `Ready` to call `TransitionTo(Passivating)` instead of inline passivation logic
- [x] 3.8 Verify: `dotnet build` passes (0 errors), `dotnet test` passes (1,400 tests, 0 failures)

## 4. Handler Module Extractions

- [x] 4.1 Deferred: SessionSubscriberManager extraction — EmitOutput touches too many call sites across the actor; subscriber management stays inline for now
- [x] 4.2 Extract `DeliveryRetryHandler` to `src/Netclaw.Actors/Sessions/Handlers/DeliveryRetryHandler.cs` — retry counting, eligibility, nudge building
- [x] 4.3 Extract `TurnStateTracker` to `src/Netclaw.Actors/Sessions/Handlers/TurnStateTracker.cs` — per-turn counters, duplicate detection
- [x] 4.4 Extract `DiscoveredToolCache` to `src/Netclaw.Actors/Sessions/Handlers/DiscoveredToolCache.cs` — MCP tool retention, lease countdown, eviction
- [x] 4.5 Extract `ProcessingWatchdog` to `src/Netclaw.Actors/Sessions/Handlers/ProcessingWatchdog.cs` — operation ID tracking, timer management
- [x] 4.6 Wire all handler modules into `LlmSessionActor` — instantiate in constructor, delegate calls
- [x] 4.7 Verify: `dotnet build` passes (0 errors), `dotnet test` passes (1,400 tests, 0 failures)

## 5. Static Pipeline Extractions

- [x] 5.1 Extract `SessionTitleGenerator` to `src/Netclaw.Actors/Sessions/Pipelines/SessionTitleGenerator.cs` — `ShouldGenerate()` and `GenerateAsync()`
- [x] 5.2 Extract `SessionCompactionPipeline` to `src/Netclaw.Actors/Sessions/Pipelines/SessionCompactionPipeline.cs` — `ExecuteAsync()`, `GenerateObservationsAsync()`, `EstimateTokens()`, `CompactionParameters` record
- [x] 5.3 Extract `SessionLlmInvoker` to `src/Netclaw.Actors/Sessions/Pipelines/SessionLlmInvoker.cs` — `InvokeAsync()`, `StreamAsync()`
- [x] 5.4 Extract `SessionToolExecutionPipeline` to `src/Netclaw.Actors/Sessions/Pipelines/SessionToolExecutionPipeline.cs` — `ExecuteToolsAsync()`, `ExecuteSingleToolAsync()`, `ClampToolResult()`, `ReviewSubAgentFinding()`, `ToolCallResult` record
- [x] 5.5 Extract `SessionRecallManager` to `src/Netclaw.Actors/Sessions/Pipelines/SessionRecallManager.cs` — recall resolution, injection, progressive exclusion, format helpers
- [x] 5.6 Wire all pipeline utilities into `LlmSessionActor` — replace direct calls with delegated calls
- [x] 5.7 Verify: `dotnet build` passes (0 errors), `dotnet test` passes (1,400 tests, 0 failures)

## 6. Final Verification and Cleanup

- [x] 6.1 Run full test suite: 1,400 tests pass, 0 failures
- [ ] 6.2 Run `dotnet slopwatch analyze` — no new violations
- [x] 6.3 Verify `LlmSessionActor.cs` line count: 2,273 lines (down from 3,208; ~930 lines extracted)
- [x] 6.4 Verify persistence wire compatibility — Events.cs and SessionSnapshot.cs unchanged
- [x] 6.5 `InternalsVisibleTo` already includes `Netclaw.Actors.Tests` — no change needed
