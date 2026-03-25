## MODIFIED Requirements

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach informed
by cross-SDK research (OpenAI, LangChain, Semantic Kernel, Anthropic, Google
ADK). Before and after compaction boundaries, the session SHALL emit
high-priority memory checkpoints into the durable memory queue instead of
performing a synchronous one-off memory flush that depends on the turn path
completing all curation work inline.

Compaction logic SHALL be encapsulated in a `SessionCompactionPipeline` static
utility class, separate from the session actor. The pipeline accepts a
`SessionState` snapshot and `CompactionParameters` record and sends results
back to the actor via `self.Tell()`.

#### Scenario: Compaction threshold reached

- **GIVEN** `UsageDetails.InputTokenCount` exceeds the compaction token limit
  derived from `ModelCapabilities.CompactionTokenLimit(SessionTuning.CompactionThreshold)`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` phase via `TransitionTo(Compacting)`
- **AND** incoming messages are buffered during compaction

#### Scenario: Compaction boundary emits memory checkpoint

- **GIVEN** compaction is about to run or has just completed a summary reduction
- **WHEN** the compaction boundary is reached
- **THEN** the session enqueues a high-priority memory checkpoint for durable
  curation
- **AND** the user-facing session does not wait for background curation to
  finish

#### Scenario: Tiered compaction — tool result clearing first

- **GIVEN** compaction is triggered
- **WHEN** phase 1 runs
- **THEN** old tool results are replaced with placeholders
- **AND** the N most recent tool interactions are preserved in full
  (configurable via `SessionTuning.KeepRecentToolResults`)
- **AND** if threshold is now satisfied, no summarization LLM call is made

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) did not bring context under threshold
- **WHEN** phase 2 runs
- **THEN** a structured summarization LLM call is made
- **AND** a `SessionCompacted` event is persisted
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** compaction runs
- **THEN** tool call/result pairs are never orphaned
- **AND** older tool interactions remain representable for checkpoint extraction
  and summarization

### Requirement: Decoupled immutable session state

The system SHALL maintain conversation state (history, turn count, title) in an
immutable `SessionState` record decoupled from the actor. State transitions
SHALL be pure functions (`Apply` methods) testable without an ActorSystem.

Session actor internal concerns SHALL be decomposed into independently testable
modules:

- **SessionSubscriberManager**: Owns subscriber registration, deregistration,
  filtered output delivery, and watch lifecycle.
- **DeliveryRetryHandler**: Owns retry counting, eligibility tracking, and
  nudge message construction for channel delivery failures.
- **TurnStateTracker**: Owns per-turn transient counters (tool call count,
  budget nudge, duplicate detection). Provides `Reset()` for turn boundaries.
- **DiscoveredToolCache**: Owns MCP tool retention with lease countdown,
  eviction, and max count enforcement.
- **ProcessingWatchdog**: Owns operation ID tracking, timer management, and
  expiry validation for stuck-operation detection.

Each module SHALL be a plain `internal sealed` class instantiated by the actor,
not registered in DI.

#### Scenario: State transitions are pure and testable

- **GIVEN** a `SessionState` instance
- **WHEN** an event is applied via `Apply()`
- **THEN** a new `SessionState` is returned with the event applied
- **AND** the original instance is not modified

#### Scenario: Handler modules testable without ActorSystem

- **GIVEN** a `TurnStateTracker` instance
- **WHEN** `Reset()` is called
- **THEN** all per-turn counters are zeroed
- **AND** no Akka.NET types are required for the test

### Requirement: Automatic pre-turn memory recall

The session system SHALL run automatic durable-memory recall before each
user-facing model turn. Recall logic SHALL be encapsulated in a
`SessionRecallManager` class that owns the turn recall cache and progressive
exclusion set. The manager SHALL provide `ResolveForTurn()`,
`InjectIntoMessages()`, `ResetForNewTurn()`, and `ResetForCompaction()` methods.

The recall pipeline SHALL use the incoming user message, recent turn state,
active project/session context, and policy scope to assemble a bounded recall
bundle. If recall exceeds its latency budget or the memory substrate is
unhealthy, the turn SHALL continue in degraded mode without blocking on recall.

#### Scenario: User-facing turn receives automatic recall bundle

- **GIVEN** a session receives a new user message
- **WHEN** the turn pipeline prepares the model request
- **THEN** the `SessionRecallManager` resolves recall before the model call
- **AND** injects a bounded recall bundle when eligible memories are found

#### Scenario: Recall timeout degrades safely

- **GIVEN** the memory recall pipeline exceeds its configured time budget
- **WHEN** the session is preparing the next model call
- **THEN** the session continues without the recall bundle
- **AND** records degraded memory status for diagnostics and observability

### Requirement: Session title generation

Title generation SHALL be encapsulated in a `SessionTitleGenerator` static
utility class. The generator SHALL determine whether to generate a title based
on turn count and `SessionTuning.TitleGenerationInterval`, and fire a sidecar
LLM call that sends `TitleGenerationCompleted` back to the actor. Title
generation is best-effort — failures are silently logged and do not affect
session operation.

#### Scenario: Title generated on first turn

- **GIVEN** a session completes turn 1
- **WHEN** `SessionTitleGenerator.ShouldGenerate(1, interval)` is evaluated
- **THEN** the result is `true`
- **AND** a sidecar LLM call is fired to generate a title

#### Scenario: Title generation failure is silent

- **GIVEN** the sidecar LLM call for title generation fails
- **WHEN** the error is caught
- **THEN** a warning is logged
- **AND** the session continues without a title update

### Requirement: LLM invocation encapsulation

LLM call execution and streaming SHALL be encapsulated in a `SessionLlmInvoker`
static utility class. The invoker SHALL handle timeout wrapping, streaming delta
forwarding via `self.Tell(LlmResponseDeltaReceived)`, and error packaging as
`LlmCallFailed`. Dynamic context layer injection SHALL be a static method on
this class.

#### Scenario: LLM call timeout produces LlmCallFailed

- **GIVEN** an LLM call is in progress
- **WHEN** the configured `TurnLlmTimeout` elapses
- **THEN** the invoker sends `LlmCallFailed` with a `TimeoutException` to the actor

#### Scenario: Streaming deltas forwarded to actor

- **GIVEN** an LLM streaming call is in progress
- **WHEN** text content chunks arrive
- **THEN** each chunk after the first is forwarded as `LlmResponseDeltaReceived`
- **AND** the first chunk is held until the second arrives (single-chunk optimization)

### Requirement: Tool execution encapsulation

Tool execution SHALL be encapsulated in a `SessionToolExecutionPipeline` static
utility class. The pipeline SHALL execute tool calls in parallel, clamp oversized
results to `SessionTuning.MaxInlineToolResultChars`, track sub-agent activity,
and send `ToolExecutionCompleted` or `ToolExecutionFailed` back to the actor.

#### Scenario: Parallel tool execution

- **GIVEN** an LLM response contains 3 tool calls
- **WHEN** `SessionToolExecutionPipeline.ExecuteToolsAsync()` runs
- **THEN** all 3 tool calls execute in parallel
- **AND** results are collected and sent as a single `ToolExecutionCompleted`

#### Scenario: Tool execution timeout

- **GIVEN** tool execution is in progress
- **WHEN** the configured `ToolExecutionTimeout` elapses
- **THEN** the pipeline sends `ToolExecutionFailed` with a `TimeoutException`
