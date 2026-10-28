# SPEC-002: Session Lifecycle and Protocol

Source PRDs: `PRD-001`
Research: `docs/research/context-management-patterns.md`

## Purpose

Define session identity, message protocol, persistence events, subscriber
model, context management, and compaction behavior for `LlmSessionActor`.

## Session Identity

- entity key: `{channelId}/{threadTs}`
- one persistent actor per Slack thread
- `SessionId` value object wraps entity key (explicit conversion only)

## Protocol Categories

- `Commands`: inbound intent from adapters or operator tooling
- `Events`: persisted domain state transitions
- `Outputs`: typed subscriber notifications filtered by `OutputFilter` bitmask

## State Architecture

Session state is decoupled from the actor into an immutable `SessionState`
record. The actor holds a single `SessionState` field and replaces it on each
event via pure `Apply` methods. Transient concerns (subscribers, message
buffer, behavior) remain on the actor.

This enables:
- Pure unit testing of state transitions without an ActorSystem
- Testable compaction and replay logic in isolation
- Future sub-agent isolation (each gets its own `SessionState`)

## Turn Lifecycle

1. `SendUserMessage` passes policy and complete input compatibility checks.
2. Actor appends the user message to `SessionState.History`.
3. Actor checks active history again before each model call.
4. Actor invokes the configured `IChatClient` via `ChatMessageConverter`.
5. Actor persists the `TurnRecorded` event and applies it to state.
6. Actor emits typed `SessionOutput` events to subscribers.
7. Actor checks the compaction threshold.

### Model Input Compatibility

The actor checks all active media references against the main model input
modalities. The check includes recovered history, new input, buffered input,
and tool-result media. An unknown persisted modality fails closed.

The actor rejects incompatible new input before it changes the session state.
It checks again before each model call to protect paths that add media during a
turn. The actor emits `ErrorCategory.InputCompatibility` with the unsupported
modalities and recovery guidance. It does not call the primary client,
fallback client, or provider when this local check fails.

### Tool Execution Pipeline

Tool-enabled sessions compose one `SessionToolExecutionPipeline` from required
execution, time, and logging services. Each admitted tool-call response
is submitted as one `SessionToolBatch`; the batch derives its immutable tool
authority from the admitted `TurnContext` and carries environment and
per-batch capabilities separately. Callers cannot supply a second authority
object that disagrees with the admitted turn.

The pipeline executes calls concurrently with fresh invocation state per call.
Interactive approval is a required capability union: unavailable, or available
with its required bridge. Tool-call and tool-result observability uses the
existing session transcript path rather than a parallel no-op audit sink.
Unavailable background-job infrastructure is an explicit capability state and
retains synchronous execution behavior. This internal composition does not
change MCP schemas, persisted actor messages, approval outcomes, or model-facing
tool results.

### Tool Outcomes Without Progress

`TurnStateTracker` owns an actor-local count of failed calls since the last
successful batch. This rule supports PRD-001 and PRD-006. See
[the engineering glossary](GLOSSARY.md) for shared terms.

The tracker counts each actual failed call, including duplicates in one batch.
It also keeps a weighted score and an escalation stage.
A `NotFound` result adds half a point because a missing path can guide discovery.
Each other failure adds one point. `InvalidInput` and `AccessDenied` therefore reach advice after four calls.
The nudge text reports the actual call count, regardless of category.

Any actual success in a batch resets the count, score, and stage.
Synthetic cycle corrections and pending-job receipts neither increase nor reset them.
The parent journal stores each receipt category and its synthetic and pending-job flags.
An approval re-drive preserves the full batch and observes each outcome once at completion.
A legacy sibling without receipt facts causes explicit batch abandonment before the re-drive.
A child park stores the count, score, stage, and any sibling success or failure through the protobuf mapper.
A sibling success applies until the feedback batch completes.

The batch owner observes outcomes before it selects a nudge.
Scores of 4, 6, and 8 select advice, escalation, and a quiet end of the turn.
One completed batch advances at most one stage, so a wide batch cannot skip advice.
The true count and score retain all failures, including those beyond the next threshold.
The next response with tool calls ends the turn at the final stage.
The model can still give a final text response.
This rule adds no iteration limit or per-call refusal.

The advice selector compares severity before detector priority.
At equal severity, action advice wins over cause advice, and cause advice wins over budget advice.
The cause detector retains six completed feedback batches and escalates only advice that the model receives.
A batch without that cause retires its active advice. Recent evidence can still trigger a later first nudge.
A consent park preserves the history, delivered advice, and unfinished cause facts through protobuf.
See [the loop contract](tool-loop-detection.md) for examples and the complete decision flow.

For example, four failures across four tools produce a progress nudge.
Three failed edit attempts followed by a success produce no progress nudge.

```text
complete dispatch
  -> observe actual outcomes once
  -> reset on success, otherwise add failures
  -> select one nudge from the stage, actual count, and repeat decision
  -> ask the model for its next response
  -> end quietly at the final stage if the response requests tools
```

### Working Context and Child Runs

For Team and Personal turns with a declared project directory, the session
captures Git working context asynchronously before invoking the model. Git
inspection has one aggregate deadline and produces an explicit available,
not-repository, or unavailable result. Public turns and turns without a project
directory do not launch Git. Continuations carry a generation number so a late
inspection from a cancelled or superseded call cannot mutate the active turn.

Each admitted subagent receives a `ChildRunScope`: a fork of immutable tool
authority plus the parent's working-context snapshot. The child owns fresh
activity tracking and mutable tool-call state; neither is shared with the
parent or sibling runs. Terminal results use typed completion variants.
Completed and partial runs carry a `WorkingContextDelta`; failed and cancelled
runs cannot carry one. The parent merges only files the child confirms it
changed through first-party tools. Git-observed dirty files remain diagnostic
context and are never attributed to the child.

## Subscriber Model

Subscribers join via `JoinSession` with an `OutputFilter` bitmask controlling
which output categories they receive:

- `Text` — user-facing text replies
- `Thinking` — reasoning tokens (e.g., Claude extended thinking)
- `ToolCalls` — tool call requests and results
- `Usage` — token usage with context window metadata

Lifecycle messages (`TurnCompleted`, `ErrorOutput`, `SessionTitleOutput`) are
always delivered regardless of filter.

`UsageOutput` includes `ContextWindowTokens` and `UsagePercent` so subscribers
can display context consumption without duplicating session config.

## Behavior States

```
Ready → (user message) → Processing → (LLM response) → [threshold check]
                                                              │
                                                    under threshold → Ready
                                                    over threshold  → Compacting
```

- **Ready**: accepts user messages, fires LLM call, transitions to Processing.
- **Processing**: buffers incoming messages, waits for LLM response.
- **Compacting**: buffers incoming messages, runs tiered compaction sequence.

All three states handle `JoinSession`, `LeaveSession`, and snapshot messages.

## Compaction Lifecycle

Informed by cross-SDK research (see `docs/research/context-management-patterns.md`).
Uses a tiered approach following Anthropic's recommended hierarchy.

### Trigger

Token-count threshold from `UsageDetails.InputTokenCount` compared against
`SessionConfig.CompactionTokenLimit` (= `ContextWindowTokens * CompactionThreshold`).
Checked after each `TurnRecorded` persist callback.

### Tiered Compaction Sequence

**Phase 1: Tool result clearing** (cheapest, no LLM call)
- Replace old tool results with placeholders ("result cleared")
- Keep N most recent tool interactions in full detail
- Preserves reasoning/action history
- Re-check threshold — may be sufficient without summarization

**Phase 2: Extractive reduction and observation**
- Keep recent messages and preserve tool call/result pairs.
- Use the compaction model to observe discarded history.
- Keep a prior summary when a later compaction runs.
- Persist the `SessionCompacted` event.
- Request a high-priority memory checkpoint when the actor resolves an audience.
- Take a persistence snapshot.
- Emit a compaction notification to subscribers.

Background curation uses the checkpoint. The session does not wait for curation.
See [the current memory contract](../../openspec/specs/netclaw-agent-memory/spec.md).

### Compaction Model

Optional `CompactionModelId` in `SessionConfig`. Defaults to the session's
primary model. Allows routing compaction to a cheaper/faster model.

### Tool Call/Result Pair Integrity

During compaction, tool call/result pairs must remain atomic. Never orphan
a tool call from its result. Tool interactions older than the retention window
are summarized as "Used {tool} for {purpose} → {outcome}".

## Persistence Rules

- protobuf-net serialization only for events and snapshots
- framework-owned message envelopes only
- no direct persistence of `Microsoft.Extensions.AI` model types
- system prompt is always slot 0 in history — compaction must preserve it

## Persistence Events

| Event | Purpose |
|-------|---------|
| `SystemPromptSet` | System prompt set or replaced |
| `TurnRecorded` | Completed turn (user message + assistant reply) |
| `SessionTitleSet` | Title generated or updated |
| `SessionCompacted` | History compacted with summary + retained messages |

## Snapshot

`SessionSnapshot` captures `History`, `TurnCount`, `Title` for fast recovery.
Taken periodically per `SessionConfig.SnapshotInterval` and after compaction.
