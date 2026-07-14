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

1. `SendUserMessage` command accepted after policy checks.
2. Actor appends user message to `SessionState.History`.
3. Actor invokes configured `IChatClient` via `ChatMessageConverter`.
4. Actor persists `TurnRecorded` event and applies to state.
5. Actor emits typed `SessionOutput` events to subscribers.
6. Actor checks compaction threshold.

### Tool Execution Pipeline

Tool-enabled sessions compose one `SessionToolExecutionPipeline` from required
execution, audit, time, and logging services. Each admitted tool-call response
is submitted as one `SessionToolBatch`; the batch derives its immutable tool
authority from the admitted `TurnContext` and carries environment and
per-batch capabilities separately. Callers cannot supply a second authority
object that disagrees with the admitted turn.

The pipeline executes calls concurrently with fresh invocation state per call.
Unavailable background-job infrastructure is an explicit capability state and
retains synchronous execution behavior. This internal composition does not
change MCP schemas, persisted actor messages, approval outcomes, or model-facing
tool results.

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

**Phase 2: Pre-compaction memory flush** (LLM call)
- Structured extraction prompt: key facts, decisions, action items
- Persist extracted memories to external storage (MCP memorizer)
- Ensures durable context survives the lossy summarization step

**Phase 3: Structured summarization** (LLM call)
- Domain-specific section headings (not generic "summarize this"):
  - Task overview and goals
  - Current state and progress
  - Key decisions and their rationale
  - Pending actions and blockers
  - User preferences and context to preserve
- Anchored iterative merging when prior summary exists
- Persist `SessionCompacted` event
- Take persistence snapshot
- Emit compaction notification to subscribers

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
