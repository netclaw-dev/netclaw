# Change Proposal: working-context-grounding

## Why

Netclaw sessions have no durable state for "what files the agent has
recently been working with" — that information lives entirely in the
conversation tail. When compaction contracts the tail, the agent loses
its sense of "what was I looking at?" at the mercy of the observer
LLM's summarization quality. The `compaction-rework` change (merged as
#597) improves summarization via the structured 9-section format, but
the deeper architectural gap remains: this kind of anchor state is
conversational, not structural.

Cline's production pattern (from `src/core/task/focus-chain/`) is the
clearest precedent: a markdown todo list tracked on disk, watched via
chokidar, and re-attached to every LLM call — it survives compaction
by being *re-read*, not *re-summarized*. Cline's summarization prompt
explicitly protects it: *"If no task_progress list was included in the
previous context, you should NOT create a new task_progress list"*
(`contextManagement.ts:46-50`). This is the durable-state-next-to-
conversation pattern Netclaw should adopt.

This change adds `WorkingContext` to `SessionState` as the Netclaw
equivalent: a small immutable record carrying `RecentFiles`, a bounded
ring buffer of file paths the agent has recently read, written, or
edited. Updated by tool-execution hooks in `LlmSessionActor`, persisted
through snapshots and compacted events, and injected as a
`[working-context]` block on every LLM call adjacent to the existing
`[session]` block.

## What Changes

- **New**: `WorkingContext` immutable record with one field —
  `RecentFiles` (bounded ring buffer of up to 10 entries,
  most-recent-first, deduped on repeat access). Goals and progress
  markers were originally considered but are intentionally excluded
  from this change: they would require an observer-output parser that
  isn't part of this scope. They can be added in a follow-up change
  when that parser lands.
- **New**: `SessionState.WorkingContext` field, defaults to
  `WorkingContext.Empty`.
- **New**: `WorkingContext` is persisted in `SessionSnapshot` and
  carried on the `SessionCompacted` event so it survives compaction,
  recovery, and daemon restart.
- **New**: `InjectDynamicContextLayers` emits a `[working-context]`
  block immediately after the existing `[session]` block on every LLM
  call. Suppressed entirely when the context is empty.
- **New**: `WorkingContextUpdater` static helper extracts file paths
  from tool-call `ArgumentsJson` and updates
  `WorkingContext.RecentFiles`. Uses a field-name probe
  (`path`/`file_path`/`filePath`/`file`/`filename`/`fileName`) rather
  than a tool-name allowlist, so first-party tools, MCP filesystem
  tools, and any future path-taking tool participate without a central
  registry.
- **New**: Path sanitization in `AddRecentFile` — paths containing
  newline, carriage return, or null-byte characters are rejected. This
  is a prompt-injection defense: without it, a path with `\n` would
  break out of the `recent_files:` section in `ToContextBlock` and
  inject arbitrary content into the LLM's system prompt.
- **Modified**: `LlmSessionActor` hooks into `ToolExecutionCompleted`
  to call `WorkingContextUpdater.UpdateFromToolResults` on the
  completed batch, then re-emits the updated `[working-context]` block
  on the next LLM call.

## Capabilities

### New Capabilities

_(none — this change modifies an existing capability)_

### Modified Capabilities

- `netclaw-session`: a new requirement "Durable working context grounding"
  is added, documenting the `WorkingContext` field, its update sources,
  its persistence behavior, and the `[working-context]` injection point.

## Impact

### Affected code

- `src/Netclaw.Actors/Sessions/SessionState.cs` — add `WorkingContext`
  field and plumb through snapshot + event
- `src/Netclaw.Actors/Sessions/WorkingContext.cs` — new immutable record
- `src/Netclaw.Actors/Sessions/WorkingContextUpdater.cs` — new static
  helper for tool-call path extraction
- `src/Netclaw.Actors/Protocol/SessionSnapshot.cs` — persist
  `WorkingContext`
- `src/Netclaw.Actors/Protocol/Events.cs` — extend `SessionCompacted`
  with `WorkingContext` field
- `src/Netclaw.Actors/Sessions/LlmSessionActor.cs` — tool-execution
  hook updates `WorkingContext`; `InjectDynamicContextLayers` emits
  `[working-context]` block via `WorkingContext.ToContextBlock()`

### Affected tests

- `src/Netclaw.Actors.Tests/Sessions/WorkingContextTests.cs` (new) —
  ring-buffer behavior, dedup-on-repeat, immutable update, default
  empty, control-character rejection, ProtoBuf round-trip
- `src/Netclaw.Actors.Tests/Sessions/WorkingContextUpdaterTests.cs` (new) —
  path extraction from ArgumentsJson, tool-call → tool-result lookup,
  orphan handling, malformed JSON handling
- `src/Netclaw.Actors.Tests/Sessions/CompactionIntegrationTests.cs` —
  integration test: `[working-context]` block appears in the next LLM
  call after a file-taking tool completes; working context survives
  compaction; working context restored after actor recovery

### Affected APIs / journals

- **Journal compatibility**: existing `SessionCompacted` events replay
  cleanly — the new `WorkingContext` field is optional and defaults to
  null, which `SessionState.Apply(SessionCompacted)` maps to
  `WorkingContext.Empty`.
- **Snapshot compatibility**: existing snapshots replay cleanly via the
  same default-empty semantics. No migration required.
- **IPC**: no public API change visible outside the actor package.

### Security & operational impact

- **Security**: `AddRecentFile` rejects paths containing control
  characters as a prompt-injection defense. Without it, an
  attacker-crafted path (e.g. via a user message processed by a tool)
  could break out of the `[working-context]` block and inject
  instructions into the LLM's system prompt. The sanitization runs
  before any persistence or rendering.
- **Operational**: small increase in snapshot and event size —
  `WorkingContext` is bounded at 10 file paths. Negligible.
- **Observability**: `SessionCompacted` event carries `WorkingContext`,
  which is useful for post-hoc debugging of "what files the agent had
  recently touched" at compaction boundaries.

### Dependencies / out of scope

- **Stacks on `compaction-rework`** (merged as #597) — this change
  assumes the structured 9-section summary format, the
  `[session-summary session:{id}]` header, and the user-message-boundary
  reducer from that change are in place.
- **Out of scope**: `OpenGoals` / `ProgressMarkers` fields and the
  observer-output parser that would populate them. These were
  considered for this change but deferred — they require a new
  structured-summary parser which is its own unit of work. Can be
  added in a follow-up change when that parser lands.
- **Out of scope**: `CurrentWorkingDirectory`, `ActiveProjectPath`, and
  project-scoped identity file (`CLAUDE.md` / `AGENTS.md`) re-reading —
  those are GitHub issue **#595** on milestone 0.12.
- **Out of scope**: Authoritative session CWD for path-taking tool
  calls (shell, file_read, file_write, file_edit) — GitHub issue
  **#596**.
- **Out of scope**: Files-as-source-of-truth refactor — Aider's design
  pattern of re-reading files from disk every turn instead of
  persisting tool results in history. Tracked in the research plan
  for separate discussion.
