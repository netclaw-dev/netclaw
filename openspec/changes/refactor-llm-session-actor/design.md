## Context

`LlmSessionActor` is a 3,100-line `ReceivePersistentActor` that is the single persistent
entity managing per-session LLM conversation state. It receives `SendUserMessage`, invokes
`IChatClient`, persists `TurnRecorded` events, and routes typed `SessionOutput` events to
filtered subscribers. It currently mixes 10+ concerns and has 19 constructor parameters.

The actor uses three implicit `Become()` states (Ready, Processing, Compacting) with shared
handler registration via `CommandSubscriptionMessages()`. Adding a fourth state (Passivating)
for graceful drain (#326) is blocked by this implicit structure because handler registration
must be duplicated across states.

`SessionConfig` carries 16+ properties conflating model capabilities (runtime-derived),
user-facing settings, feature flags, and internal tuning constants. This makes it unclear
what operators should configure and drags unnecessary coupling into every consumer.

The actor is backed by Akka.Persistence with 4 event types (`SystemPromptSet`,
`TurnRecorded`, `SessionTitleSet`, `SessionCompacted`) and `SessionSnapshot`, all
protobuf-serialized. These must remain wire-compatible.

Three child actors exist: `SessionLogActor` (file logging), `MemoryCurationActor`
(evaluate-before-write memory pipeline), `SessionMemoryObserverActor` (idle-triggered
memory distillation). All are created in `RecoveryCompleted`.

DI resolution uses Akka.Hosting's `resolver.Props<LlmSessionActor>(entityId)` which
automatically resolves constructor parameters from `IServiceProvider`.

## Goals / Non-Goals

**Goals:**

- Decompose `SessionConfig` into `ModelCapabilities`, `SessionTuning`, and slimmed
  `SessionConfig` with clean DI boundaries
- Reduce constructor parameters from 19 to ~6 via composite dependency records
- Formalize the state machine with an explicit `SessionPhase` enum and validated
  transitions, including a new `Passivating` state
- Extract 10 handler/pipeline modules that are independently testable without an
  ActorSystem
- Reduce `LlmSessionActor` from ~3,100 to ~800-1,000 lines
- Preserve all existing behavioral requirements and persistence wire format
- Create clean seams for future Akka.Agents framework extraction

**Non-Goals:**

- Akka.Agents package extraction (future work after this refactoring)
- Memory observer passivation-triggered distillation (separate OpenSpec)
- Graceful daemon restart implementation (#326 — this creates the prerequisite)
- PipelineHostingActor base class extraction (#306 — separate concern)
- New test coverage for extracted modules (desirable but not gating)
- Changing persistence event types or snapshot format

## Decisions

### D1: Horizontal extraction, not inheritance

**Decision:** Extract concerns into handler modules (plain classes) and static pipeline
utilities. The actor remains a thin coordinator. No base class.

**Rationale:** A base class would force an inheritance hierarchy that constrains future
Akka.Agents consumers. Composition via dependency records allows different agent
implementations to pick which modules they need. The actor's persistent state management
(event sourcing, snapshots, recovery) is inherently tied to `ReceivePersistentActor` —
wrapping it in a base class adds indirection without value.

**Alternative considered:** Abstract `SessionActorBase` with virtual hooks for each
concern. Rejected because Akka actors already have a rich lifecycle (PreStart,
PostStop, PreRestart) and adding virtual dispatch on top of `Become()` creates
confusing double-dispatch.

### D2: SessionConfig clean break (no deprecation period)

**Decision:** Remove old flat properties from `SessionConfig` immediately. No
`[Obsolete]` forwarding. Update all consumers in one pass.

**Rationale:** Netclaw is pre-1.0. There are no external consumers of `SessionConfig`.
All ~20 test sites and the daemon `Program.cs` are updated in the same PR. A deprecation
period adds complexity (forwarding properties, dual paths) for zero benefit.

**Types after decomposition:**

```csharp
// Runtime-derived from model capability resolution. Never from user config.
public sealed record ModelCapabilities
{
    public string ModelId { get; init; } = string.Empty;
    public int ContextWindowTokens { get; init; } = 32_768;
    public ModelModality InputModalities { get; init; } = ModelModality.Text;
    public ModelModality OutputModalities { get; init; } = ModelModality.Text;

    public int CompactionTokenLimit(double threshold) => (int)(ContextWindowTokens * threshold);
}

// Internal tuning constants. Bindable from config for testing but undocumented.
public sealed record SessionTuning
{
    public double CompactionThreshold { get; init; } = 0.75;
    public int SnapshotInterval { get; init; } = 20;
    public string? CompactionModelId { get; init; }
    public int KeepRecentToolResults { get; init; } = 3;
    public int MaxInlineToolResultChars { get; init; } = 12_000;
    public int DiscoveredToolRetentionTurns { get; init; } = 3;
    public int DiscoveredToolMaxCount { get; init; } = 12;
    public int KeepRecentMessages { get; init; } = 6;
    public int TitleGenerationInterval { get; init; } = 10;

    // Feature flags scheduled for removal. Both always true in production.
    public bool MemorySidecarsEnabled { get; init; } = true;
    public bool DeterministicRetrievalEnabled { get; init; } = true;
}

// User-facing operational settings.
public sealed record SessionConfig
{
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public int MaxToolCallsPerTurn { get; init; } = 30;
    public int MemoryObserverIdleSeconds { get; init; } = 90;
    public TimeSpan TurnLlmTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan ToolExecutionTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan SidecarLlmTimeout { get; init; } = TimeSpan.FromSeconds(90);
    public SessionTuning Tuning { get; init; } = new();
}
```

### D3: Composite dependency records for constructor reduction

**Decision:** Group 19 constructor parameters into 4 composite records plus
`ModelCapabilities` and `SessionConfig`.

```csharp
public sealed record SessionServices(
    IChatClientProvider ClientProvider,
    ISystemPromptProvider PromptProvider,
    IReadOnlyList<IContextLayerProvider> ContextLayers,
    TimeProvider TimeProvider,
    NetclawPaths? Paths);

public sealed record SessionToolServices(
    IToolExecutor ToolExecutor,
    IToolAuditLogger? AuditLogger,
    ToolRegistry ToolRegistry,
    ToolAccessPolicy? AccessPolicy,
    TrustContextDeriver? TrustDeriver,
    SkillRegistry? SkillRegistry);

public sealed record SessionMemoryServices(
    IMemoryExtractor MemoryExtractor,
    IMemoryRecallCoordinator RecallCoordinator,
    IMemoryCheckpointSink CheckpointSink,
    SQLiteMemoryStore? MemoryStore);

public sealed record SessionObservability(
    ISessionMetrics? Metrics,
    ISessionLifecycleObserver? LifecycleObserver);
```

**Rationale:** Each record groups cohesive dependencies. `SessionToolServices` is nullable
for tool-less sessions. Records are registered in DI as singletons, resolved automatically
by Akka.Hosting's `resolver.Props<>()`.

**Alternative considered:** Keep flat parameters, add a builder/factory. Rejected because
it doesn't reduce the constructor surface — just moves the problem.

### D4: Explicit SessionPhase enum with TransitionTo() validation

**Decision:** Add a `SessionPhase` enum and a `TransitionTo(SessionPhase)` method that
validates legal transitions, sets the phase, calls `Become()`, and logs.

```csharp
public enum SessionPhase
{
    Recovering,   // During journal replay
    Ready,        // Accepts user messages, idle timeout active
    Processing,   // LLM call or tool execution in flight
    Compacting,   // Context compaction running
    Passivating   // Draining: final distillation, then stop
}
```

**Legal transitions:**
- `Recovering → Ready` (after RecoveryCompleted)
- `Ready → Processing` (user message accepted)
- `Ready → Compacting` (shouldn't happen directly, but guard it)
- `Ready → Passivating` (idle timeout with no subscribers)
- `Processing → Ready` (turn complete, no compaction needed)
- `Processing → Compacting` (compaction threshold reached)
- `Compacting → Ready` (compaction complete, no buffered messages)
- `Compacting → Processing` (compaction complete, buffered messages exist)
- `Passivating` is terminal (no transitions out)

**Rationale:** `Become()` stays as the runtime dispatch mechanism — it's how Akka works.
The enum adds a validation and observability layer. Illegal transitions throw
`InvalidOperationException` (fail loud, per CLAUDE.md). The observer actor and metrics
can react to phase changes.

**Alternative considered:** Full FSM framework (Akka.NET has `FSM<TState, TData>`).
Rejected because `ReceivePersistentActor` is the required base class for persistence,
and multiple inheritance isn't possible. A lightweight enum + validation achieves the
same benefit without framework coupling.

### D5: Passivating behavior protocol

**Decision:** The `Passivating` state follows this protocol:

1. `Ready` receives `ReceiveTimeout` with no active subscribers
2. `TransitionTo(Passivating)` — buffers new `SendUserMessage`, disables idle timeout
3. If `_observerActor` exists: send `RequestFinalDistillation`, start 5s timer
4. Wait for `SessionDistillationCompleted` or `PassivationTimeout`
5. Save snapshot, notify `_lifecycleObserver?.OnSessionDeactivated()`, `Context.Stop(Self)`

If no observer actor exists (no memory store configured), skip step 3-4 and proceed
directly to snapshot + stop.

**Rationale:** The 5s timeout prevents passivation from hanging indefinitely if the
observer is stuck. The observer already has the transcript accumulated — it just needs
a trigger to distill immediately instead of waiting for its own idle timer.

### D6: Handler modules are plain classes, not DI-registered

**Decision:** Extracted handler modules (`SessionSubscriberManager`, `DeliveryRetryHandler`,
`TurnStateTracker`, `DiscoveredToolCache`, `ProcessingWatchdog`) are instantiated directly
by the actor in its constructor. They are `internal sealed` classes with
`InternalsVisibleTo` for the test project.

**Rationale:** These modules own per-session transient state. They have no lifecycle
independent of the actor. DI registration would be unnecessary indirection — the actor
knows exactly what it needs. Plain classes are the simplest thing that works.

### D7: Static pipelines for async fire-and-forget work

**Decision:** `SessionTitleGenerator`, `SessionCompactionPipeline`, `SessionLlmInvoker`,
`SessionToolExecutionPipeline` remain `internal static` classes. They accept explicit
parameters (no field access) and send results back via `self.Tell()`.

**Rationale:** These methods are already static in the current code — they capture all
needed state as method parameters and run on the thread pool. Extracting them to separate
files is a mechanical relocation that doesn't change behavior. The pattern is safe because
each method captures only immutable values and an `IActorRef self` for reply.

`SessionRecallManager` is the exception — it owns mutable state (`_turnRecallCache`,
`_injectedMemoryIds`) but is only accessed from the actor's mailbox thread. It's a plain
class, not static.

### D8: JSON schema enforcement

**Decision:** Update `netclaw-config.v1.schema.json` Session section from
`additionalProperties: true` to explicit property list with `additionalProperties: false`.
Add nested `Tuning` object schema.

Config-file keys stay as `XxxTimeoutSeconds` (int) for user-facing JSON. Post-bind
conversion to `TimeSpan` happens in a static factory method on `SessionConfig`:

```csharp
public static SessionConfig BindFromConfiguration(IConfigurationSection section)
{
    var raw = section.Get<RawSessionConfig>() ?? new();
    return new SessionConfig
    {
        IdleTimeout = raw.IdleTimeout,
        MaxToolCallsPerTurn = raw.MaxToolCallsPerTurn,
        MemoryObserverIdleSeconds = raw.MemoryObserverIdleSeconds,
        TurnLlmTimeout = TimeSpan.FromSeconds(Math.Max(1, raw.TurnLlmTimeoutSeconds)),
        ToolExecutionTimeout = TimeSpan.FromSeconds(Math.Max(1, raw.ToolExecutionTimeoutSeconds)),
        SidecarLlmTimeout = TimeSpan.FromSeconds(Math.Max(1, raw.SidecarLlmTimeoutSeconds)),
        Tuning = section.GetSection("Tuning").Get<SessionTuning>() ?? new()
    };
}
```

**Rationale:** Users write `"TurnLlmTimeoutSeconds": 180` (familiar, no format ambiguity).
Internal code uses `TimeSpan` (no repeated `TimeSpan.FromSeconds(Math.Max(1, ...))` guards).
The bind method validates once at startup.

## Risks / Trade-offs

**[Risk] Test churn from SessionConfig decomposition** → ~20 test files construct
`new SessionConfig { ... }`. Mitigation: Tests use `new SessionConfig()` (defaults are
sensible) and override only what they need via `with { }`. A single pass updates all sites.

**[Risk] Akka.Hosting DI resolution with composite records** → `resolver.Props<>()` resolves
all constructor params from DI. Composite records must be registered as singletons.
Mitigation: Verify registration in `NetclawAkkaHostingExtensions.WithSessionManager()` and
add an integration test that resolves `Props` successfully.

**[Risk] Handler module state leaks across turns** → Extracted handlers own transient state
that must be reset at turn boundaries. Mitigation: Each handler has an explicit `Reset()`
method. The actor calls resets at well-defined points (turn start, compaction boundary).
`TransitionTo()` can assert state invariants.

**[Risk] Regression in static pipeline extractions** → Mechanical relocation of already-static
methods. Mitigation: These methods are already tested through integration tests. No behavior
change — just file relocation. `InternalsVisibleTo` preserves test access.

**[Risk] Passivating state blocks future messages** → If an active subscriber sends a message
during passivation, it gets buffered and never processed. Mitigation: Passivation only
triggers when `_subscribers.Count == 0`. If a subscriber joins during passivation, the
message stays buffered — on rehydration, the actor recovers and processes it. This is
acceptable because passivation is a 5s window and rehydration is fast.

**[Trade-off] More files, smaller classes** → 10 new files under `Handlers/` and
`Pipelines/`. This increases file count but each file has a single focused responsibility.
Navigation is aided by the directory structure and IDE tooling.

**[Trade-off] SessionRecallManager is stateful but not an actor** → It owns mutable state
accessed only from the actor's mailbox thread. This is safe by Akka's single-threaded
mailbox guarantee but could be confusing. Mitigated by clear documentation and the fact
that it's `internal` with no public surface.
