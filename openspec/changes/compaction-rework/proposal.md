# Change Proposal: compaction-rework

## Why

Netclaw's compaction pipeline exhibited post-compaction grounding failures in a
live Slack session on 2026-04-11 — ArdyBot lost specificity around a `Rect`
struct it had been inspecting, and failed to recall its own session ID because
an earlier turn had referenced a foreign session ID. Root-cause analysis showed
the observer phase strips the grounding signal it most needs (tool-call
arguments, self-session-id disambiguation), produces a free-form bullet list
that decays exponentially across successive compactions, and relies on an
extractive reducer that can create orphan tool-result messages the provider
will reject.

Rather than another incremental prompt patch, this change is a structural
rework informed by reading the actual source of four real LLM harnesses:
**OpenCode** (SST, `2719063`), **Aider** (`f09d706`), **Cline** (`a0faf7c`),
and **Claude Code** (Anthropic — docs at `code.claude.com/docs/en/*`). The
research surfaced three ideas worth adopting: Cline's 9-section structured
summary with explicit anti-drift rules, Cline's monotonic compaction boundary
that prevents summary-over-summary decay, and OpenCode's
truncate-only-at-user-message-boundaries rule that cleanly enforces tool
pair integrity. The current netclaw-session spec already has a "Tool call/result
pair integrity" scenario that the code does not honor — this change fixes that
gap as well.

Research write-up with file/line citations is in the plan file at
`/home/petabridge/.claude/plans/proud-honking-goose.md`.

## What Changes

- **BREAKING**: `CompactionParameters` record gains a `SessionId` field. All
  callers of `SessionCompactionPipeline.ExecuteAsync` must pass it.
- **BREAKING**: `ObservationPromptBuilder.BuildObservationSystemPrompt()`,
  `BuildObservationUserPrompt()`, and `WrapObservations()` gain `SessionId`
  parameters and a new output contract (structured sections, not free-form
  bullets).
- **BREAKING**: The compacted session history format changes. Prior
  observation messages stored as `User`-role messages with `[observations
  from earlier in this session]` prefix now use a distinctive
  `[session-summary session:{id}]` header. The header is the recognition
  marker used by the observer on successive compactions and by the reducer
  when walking backward to a user-message boundary. Journals written
  before this change still replay via the existing `Apply(SessionCompacted)`
  path — the old format stays readable — but new compactions produce the
  new format.
- **New**: Observer LLM system prompt rewritten to the 9-section structured
  format, borrowed from Cline's `contextManagement.ts:10-110`:
  Primary Request / Technical Concepts / Files+Code / Problem Solving /
  Pending Tasks / Task Evolution (with direct user quotes to prevent drift) /
  Current Work / Next Step / Required Files. Adapted to Netclaw vocabulary.
- **New**: Observer receives the self `SessionId` in its system prompt so it
  can disambiguate the running session from any foreign session IDs
  referenced in the discarded window.
- **New**: Observer is instructed: "If the input already contains a
  `[session-summary]` block from a prior compaction, preserve its sections
  verbatim and append/update — do not rewrite." This is the structural
  second-compaction defense.
- **Modified**: `ExtractiveSessionReducer` truncates only at user-message
  boundaries. When the naive cutoff would land on a `Tool`-role message or
  an `Assistant` message with `FunctionCallContent`, walk backward to the
  nearest user-message boundary. Subsumes the latent pair-integrity bug in
  the current reducer which slices by count and produces orphan tool
  results.
- **Modified**: `netclaw-session` "Conversation compaction" requirement —
  updated to reflect the new structured summary format, the distinctive
  `[session-summary session:{id}]` header, and user-boundary truncation.
  The "Tool call/result pair integrity" scenario becomes enforced (was
  aspirational).

## Capabilities

### New Capabilities

_(none — this change modifies an existing capability)_

### Modified Capabilities

- `netclaw-session`: the "Conversation compaction" requirement is rewritten
  to reflect structured 9-section summary output, the
  `[session-summary session:{id}]` header that makes summaries
  recognizable across successive compactions, and tool call/result pair
  integrity via user-boundary truncation. The existing "Tool call/result
  pair integrity during compaction" scenario is strengthened from
  aspirational to strictly enforced.

## Impact

### Affected code

- `src/Netclaw.Actors/Sessions/ObservationPromptBuilder.cs` — prompt rewrite,
  new signatures, structured section output contract
- `src/Netclaw.Actors/Sessions/Pipelines/SessionCompactionPipeline.cs` —
  `CompactionParameters.SessionId`, emit summary as System-role with
  boundary marker
- `src/Netclaw.Actors/Sessions/SessionState.cs` — update
  `Apply(SessionCompacted)` to preserve `WorkingContext` and rebuild
  history with the structured summary at the head
- `src/Netclaw.Actors/Sessions/ExtractiveSessionReducer.cs` — walk backward
  to user-message boundary instead of slicing by count
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — `CompactionParameters`
  construction includes `_sessionId`

### Affected tests

- `src/Netclaw.Actors.Tests/Sessions/ObservationPromptBuilderTests.cs` —
  assertions for structured sections, self-session-id embedding,
  preserve-prior-summary rule
- `src/Netclaw.Actors.Tests/Sessions/ExtractiveSessionReducerTests.cs` —
  user-boundary truncation, orphan prevention for tool results and
  assistant tool_calls
- `src/Netclaw.Actors.Tests/Sessions/CompactionIntegrationTests.cs` —
  monotonic boundary, second-compaction no-decay test, session-id
  disambiguation test

### Affected APIs / journals

- **Journal compatibility**: existing `SessionCompacted` events continue to
  deserialize and replay. New compactions write the extended event with the
  boundary index. No migration required.
- **IPC**: no public API change visible outside the actor package.
- **Memory queue**: unchanged — compaction still emits its high-priority
  memory checkpoint via `EnqueueCheckpointFireAndForget` per the existing
  "Compaction boundary emits memory checkpoint" scenario.

### Security & operational impact

- **Security**: none. No new trust surfaces, no new grant categories, no
  new tool capabilities. The observer LLM call continues to use the
  existing `_compactionClient` with the same audience context.
- **Operational**: slight increase in observer prompt size from the
  structured template. Offset by the existing `KeepRecentToolResults` +
  Phase 1 tool-result clearing which bounds the discarded window size. No
  change to the compaction trigger threshold.
- **Observability**: `SessionCompacted` event format extends — existing
  eval replay tooling continues to work on old journals; new journals
  carry additional metadata useful for regression analysis.

### Dependencies / out of scope

- Durable `WorkingContext` task state (`RecentFiles`, `OpenGoals`,
  `ProgressMarkers`) is a separate OpenSpec change
  (`working-context-grounding`) that stacks on this one.
- Session CWD tracking and project identity file re-reading are tracked as
  GitHub issues **#595** and **#596** against milestone 0.12, not this
  change.
- Aider's "files-as-source-of-truth" philosophy (stop persisting file
  contents in history, re-read on demand) is called out as the deepest
  architectural direction but is not in scope.
