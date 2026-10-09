# SPEC-002: Session Lifecycle and Protocol

Source PRDs: `PRD-001`
Research: `docs/research/context-management-patterns.md`

## Purpose

Define session identity, message protocol, persistence events, subscriber
model, context management, and compaction behavior for `LlmSessionActor`.
Use [the engineering glossary](GLOSSARY.md) for shared terms.

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
Completed and partial runs carry a `WorkingContextDelta`.
Cancelled runs retain confirmed activity as partial evidence and keep a failed outcome.
The parent merges a delta only from completed or partial success.
The parent attributes only files that first-party tools confirm the child changed.
Git-observed dirty files remain diagnostic context. They do not prove child authorship.

### Background Child Ownership

`LlmSessionActor` owns durable child acceptance, cancellation admission, dispatch closure, terminal receipt, and result delivery.
`SubAgentActor` owns live task execution, operation health, and confirmed partial evidence.
The live-only `ChildRunDispatch` gate serializes new model and tool dispatch against closure.
The accepted run retains its original `ChildRunScope.Authority` and initial snapshot.
Runtime shell host objects stay live-only. Recovery does not reconstruct child execution.

The framework stamps a start key before asynchronous setup.
A tool key uses the owner session, original admitted turn, and original call ID.
A slash key uses the owner session, admitted input, original turn, and framework activation slot zero.
Equivalent retries return one accepted run. Conflicting task or resolved-profile digests fail without mutation.

Schematic flow; normal policy, path checks, and approval gates remain required:

```text
prepare the start from canonical admitted authority
owner commits ChildRunAccepted before child execution or acceptance output
owner creates the child with a run-owned lifetime
owner commits Started before it dispatches the child task
adapter returns the owner's canonical acceptance JSON
parent continues independent work
child closes task dispatch and retains its terminal result
owner authenticates the child sender and generation
owner commits the terminal receipt before acknowledging the child
owner records the actual journal position as TerminalSequenceNr
owner completes enrichment and waits for the original start batch to settle
owner atomically commits delivery identity and attributed continuation input
parent adopts the continuation under its original authority and detector checkpoint
owner materializes each fresh child call/result pair in the durable adoption fold
parent continues from the recorded result or settles a retained receipt defect
```

The initial acceptance JSON contains `run_id`, `scope_id`, `state`, and `control_tool` as strings.
Its first state is `Accepted`. Its control name is `check_agent_run`.
Tool output and model history use the same formatted body.
The later result uses a fresh call/result pair. It does not create a second result for the original start call.
Durable adoption retains that pair even when the first review fails before a tool batch or turn record.
Recovery validates each ordered sibling input before it adopts the prefix.
Repeated adoption of the same pending prefix adds no duplicate pair. A changed pair fails validation.

The owner admits terminal results in journal order, even when enrichment completes in reverse order.
Equal timestamps do not change this order.
`ResultPrepared` retains the terminal's `TerminalSequenceNr`.
Recovery rejects a terminal stamp that differs from its journal position.
Snapshot recovery rejects a terminal stamp beyond the snapshot's journal position.
The owner serializes terminal writes through their acknowledgements. A queued write does not advance Akka's `LastSequenceNr`.

The deferred control requires explicit `load_tool` use and normal parent policy.
It also requires the owner session and original eligible requester.
Children and foreign callers receive a denial without target details.
Identifiers and report paths grant no authority.
After authorization, status includes the canonical `log_path` and `artifact_directory`, including for an accepted or live child.
The adapter uses the existing child storage binding. It creates no separate path authority or durable path ledger.
Normal file tools enforce file policy when the parent reads the returned log path.
The log is diagnostic evidence. It does not prove current health, dispatch closure, or completion.

Positive example: the original requester reads its held child's returned log path through `file_read` under normal policy.
Negative example: another requester receives neither target state nor paths, even with a valid run identifier.

The owner stores child approval requests and resolutions in `BackgroundChildRun.Approvals`.
These records retain the original context, child call, prompt call, and authorization attempt.
The parent-turn approval map does not own them. An ordinary parent message does not close a live child prompt.
The owner claims the exact live wait before it persists a reusable grant.
The live dispatch gate also applies to that grant operation and the child retry.
Cancellation, terminal settlement, and recovery expire unresolved child prompts.

Cancellation admission, confirmed local dispatch closure, and terminal settlement are separate lifecycle facts.
The owner commits cancellation admission before it reports `Cancelling`.
It closes the live dispatch gate, requests token cancellation, and waits for any synchronous dispatch prefix to exit.
It then commits `DispatchClosed` before it starts framework-only finalization.

The owner commits any unresolved child prompt disposition before it commits the terminal receipt.
Token cancellation can resolve a prompt earlier. Prompt resolution need not commit before `DispatchClosed`.

After closure, framework-only finalization has a separate five-second deadline.
It retains checkpoints and permits atomic report writes only inside the assigned run artifact directory.
The complete report operation runs outside the actor thread, including path checks, directory creation, serialization, and file flushes.
The owner deadline bounds its wait even when the filesystem operation ignores cancellation.
A late worker cannot publish a successful report result or replace the retained terminal outcome.
It permits no model call, task tool, project edit, external request, or approval action.
A cancelled result retains useful confirmed evidence and remains a failure outcome.
An external effect without a confirmed result remains explicitly unknown.

The first durable terminal or cancellation admission determines the terminal outcome.
A late result cannot replace an earlier cancellation outcome or admit another continuation.
The owner retains terminal results and pending delivery through snapshots and journal recovery.
Owner restart marks unresolved accepted children `Lost` and never recreates them.
The model decides its next action from the recorded evidence. The framework does not automatically retry uncertain effects.

Idle passivation defers live children and pending delivery.
Explicit stop and coordinated drain cancel children through the bounded closure path.
The inference backend owns request queues and capacity. Netclaw adds no parent or child slot coordinator.

Positive example: a held child stays live while the parent answers later input, then delivers one attributed result.
Negative example: another session obtains its run ID but cannot inspect, cancel, or read protected result paths.
Positive cancellation example: the parent receives closure evidence and a partial report after confirmed file effects.
Negative cancellation example: a late approval answer creates no grant or retry after child cancellation.

Testable contracts live in [background-subagent-runs](../../openspec/specs/background-subagent-runs/spec.md),
[netclaw-subagents](../../openspec/specs/netclaw-subagents/spec.md), and
[tool-authorization](../../openspec/specs/tool-authorization/spec.md).

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

The compaction pipeline rejects an empty task window when the observer fails or returns no summary.
The session actor reports the failure and preserves the original history and task authority.
It does not commit `SessionCompacted` or create a compaction snapshot for that failed attempt.
A valid summary permits a zero-message retention window.
A nonempty extractive window remains valid when the observer fails.
The observer prompt directs the model to preserve the current response format and each field's definition, conditions, and exceptions.
The observer call uses the existing provider intent to suppress extra model analysis.
The provider adapter selects the supported wire representation. The sidecar operation timeout does not change.

During compaction, tool call/result pairs must remain atomic. Never orphan
a tool call from its result. Tool interactions older than the retention window
are summarized as "Used {tool} for {purpose} → {outcome}".

## Persistence Rules

- registered protobuf serialization only for events and snapshots
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
| `ChildRunAccepted` | Original child authority, start identity, working facts, and parent detector evidence |

## Snapshot

`SessionSnapshot` captures `History`, `TurnCount`, `Title` for fast recovery.
Taken periodically per `SessionConfig.SnapshotInterval` and after compaction.
It also retains the child ledger, original authority, parent checkpoints, lifecycle facts, and pending delivery.
Old captured snapshots decode with an empty child ledger. Transcript text never creates a live child record.
