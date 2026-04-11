# Design: compaction-rework

## Context

Netclaw's session compaction pipeline lives in
`LlmSessionActor` + `SessionCompactionPipeline` + `ObservationPromptBuilder`
+ `ExtractiveSessionReducer`. It runs as a three-phase tiered process:
clear old tool results (phase 1), extractive reduction to keep last N
non-system messages (phase 2), and an observer LLM call that summarizes
the discarded portion into a bullet list wrapped with an
`[observations from earlier in this session]` header and inserted as a
`User`-role message at index 0 of the compacted history (phase 3).

Three failure modes were observed or confirmed:

1. **Grounding stripping**: the observer system prompt explicitly says
   "preserve tool names and outcomes but not full tool arguments/results",
   and the user-prompt renderer collapses every tool call to `[Called:
   {name}]`. Observer loses WHAT was grepped, WHAT file was read.
2. **Session-id conflation**: the observer is invoked with no knowledge of
   the self session ID, so if the discarded window references a *different*
   session ID (e.g. the agent was investigating another session via a
   tool), the observation can conflate them and produce "user asked about
   session X" without marking whether X is self or foreign.
3. **Second-compaction decay**: on the N+1'th compaction, the observation
   message from compaction N sits as a `User` message in the discarded
   window, and the observer re-summarizes its own summary plus new turns.
   Grounding loss compounds geometrically.

There is also a latent correctness bug: `ExtractiveSessionReducer` slices
history by count, which can leave a `Tool`-role message at the kept
window's first position — its matching assistant `FunctionCallContent` is
in the discarded portion, and providers (OpenAI, Anthropic) reject
messages that reference `tool_use` IDs not present in the request. The
existing `netclaw-session` spec requires pair integrity but the code does
not enforce it.

This design is informed by source-level reads of four real LLM harnesses.
Detailed research citations live in the plan file
(`/home/petabridge/.claude/plans/proud-honking-goose.md`). The three
adopted ideas are (a) Cline's 9-section structured summary template, (b)
Cline's monotonic compaction boundary that prevents re-summarization, and
(c) OpenCode's truncate-only-at-user-message-boundaries rule for pair
integrity. All three are battle-tested in production LLM harnesses.

## Goals / Non-Goals

**Goals:**

- Produce a compaction summary whose format survives multiple successive
  compactions with arithmetic (not exponential) grounding decay
- Disambiguate the self session from any foreign session IDs referenced
  in the discarded window
- Guarantee tool call/result pair integrity at the compaction boundary
  (already required by the existing spec, newly enforced in code)
- Keep the compaction trigger paths unchanged (threshold + overflow
  recovery both continue to work)
- Zero breaking changes to journal format for existing compacted sessions
  — old `SessionCompacted` events continue to deserialize and replay

**Non-Goals:**

- Durable task-state grounding (`WorkingContext` with `RecentFiles` /
  `OpenGoals` / `ProgressMarkers`) — that is the `working-context-grounding`
  change that stacks on this one
- Session `CurrentWorkingDirectory` + project-scoped identity file
  re-reading — tracked as GitHub issues #595 and #596
- Authoritative CWD for path-taking tool calls — GitHub issue #596
- Files-as-source-of-truth refactor (stop persisting file contents in
  history, re-read from disk on demand) — called out in the plan as the
  deepest architectural direction but explicitly out of scope
- Checkpoint/rollback UI — possible as a follow-up by adding an explicit
  deleted-range index (similar to Cline's approach) but no UI ships in
  this change
- Eval suite compaction regression cases — tracked separately

## Decisions

### Decision 1: Structured 9-section summary borrowed from Cline

**Chosen**: Rewrite the observer system prompt to produce output with
nine fixed sections, adapted from Cline's
`src/core/prompts/contextManagement.ts:10-110`. Sections:

1. Primary Request and Intent
2. Key Technical Concepts
3. Files and Code Sections
4. Problem Solving
5. Pending Tasks
6. Task Evolution (**with direct quotes from user messages that changed
   the task**, borrowed verbatim from Cline — this is the structural
   anti-drift rule)
7. Current Work
8. Next Step
9. Required Files (bullet list of paths the agent should re-read on resume)

**Alternatives considered**:

- *Keep free-form bullet list, just improve the prompt*: what the aborted
  PR1 tried. Middle-ground, doesn't fix the decay problem on successive
  compactions, and three of four researched systems use structured
  sections for a reason.
- *OpenCode's 5-section template* (Goal / Instructions / Discoveries /
  Accomplished / Relevant files): simpler but missing Cline's "Task
  Evolution with direct user quotes" anti-drift rule, which is the single
  most important structural defense against drift. The 9-section variant
  has been through more iterations of real-world validation.
- *Aider's free-form summarization*: correlates with the simplest design
  in the four researched systems. Works because Aider re-reads files from
  disk every turn, so conversation grounding matters less. Netclaw does
  not (yet) re-read files that way, so conversation grounding matters
  more, so we need the structure.

**Why Cline's 9-section over OpenCode's 5-section**: the Task Evolution
section with direct user quotes. The Slack failure that triggered this
rework was a drift scenario — foreign session ID mentioned earlier,
conflated after compaction. Direct user quotes are the strongest
available structural defense short of actual durable state.

### Decision 2: Distinctive summary header instead of a persisted boundary index

**Chosen**: Wrap the structured summary with a distinctive
`[session-summary session:{id}]` header. The header is the only
recognition marker — there is no separately-persisted index pointing
at the summary position. Consumers that need to find the summary walk
history looking for a User-role message whose content starts with the
header prefix. The reducer's user-message-boundary walk-back (Decision 4)
naturally preserves these messages because they are User-role and are
never cut mid-pair.

**Alternatives considered and rejected**:

- *Store the summary as a System-role message*: mid-history System
  messages confuse some providers and force the actor to track an
  explicit "this is the summary" index separately anyway.
- *Persist a `CompactionBoundaryIndex` on `SessionState`*: tried this
  initially but found zero consumers actually read it. Pure debt — an
  invariant to maintain with no present-day payoff. Header-based
  recognition gives equivalent behavior without persisting derivable
  state. A future consumer can walk history and find the last header
  on demand; at ~50-200 messages per session, this is free.
- *Cline's `conversationHistoryDeletedRange: [start, end]`*: more
  general than either of the above (supports arbitrary gaps) but
  Netclaw doesn't need gap semantics. Adopt if and when a concrete
  rollback feature needs it.

**Persistence implications**: none. No new fields on `SessionState`,
`SessionSnapshot`, or `SessionCompacted` for this decision — only the
header change in the summary message content.

### Decision 3: Observer receives self `SessionId` in its system prompt

**Chosen**: Thread `SessionId` through `CompactionParameters` →
`SessionCompactionPipeline.ExecuteAsync` →
`SessionCompactionPipeline.GenerateObservationsAsync` →
`ObservationPromptBuilder.BuildObservationSystemPrompt(SessionId)`. The
system prompt embeds `"You are summarizing a session with id {id}. This
is the self session. If observations reference OTHER session IDs from
tool calls or user content, mark them explicitly as `session:{id}` and
never conflate them with the summarizing session."`

**Alternatives considered**:

- *Let the model infer self session from tool-call context*: fragile,
  relies on the model noticing the distinction. Not reliable on smaller
  models.
- *Scrub foreign session IDs before passing to observer*: brittle
  (regex matching on ID formats), loses information the observer might
  legitimately want to reference.

### Decision 4: Truncate only at user-message boundaries — OpenCode's rule

**Chosen**: Update `ExtractiveSessionReducer` to walk backward from the
naive cutoff (`list.Count - keepCount`) until it hits a `User`-role
message that is not a system nudge (not prefixed with
`SessionState.SystemNudgePrefix`). The kept window always starts on a
user message.

**Why this works**: in a well-formed MEAI conversation, tool call/result
pairs are bookended by `User → Assistant → Tool → Assistant → User`. A
truncation that starts on a `User` message cannot split a pair — the
pair's `Assistant` (with `FunctionCallContent`) and `Tool` messages are
contiguous and either both in the kept window or both discarded. System
nudges are `User`-role messages containing recall content or empty-
response nudges; they are legitimate conversation input but not user
turns, so we skip them.

**Alternatives considered**:

- *Walk forward past orphan Tool messages* (what the aborted PR1 did):
  wrong direction. Skipping past an orphan leaves the kept window
  starting on the message *after* the orphan, which may or may not be a
  user message. Brittle.
- *Cline's explicit `ensureToolResultsFollowToolUse` pair walker*: more
  general than our need — walks the entire kept window and synthesizes
  `"result missing"` placeholders. We can get 90% of the benefit with a
  single-point backward walk because Netclaw's conversations are always
  well-formed at write time (no partial tool calls get persisted).
- *Preserve pairs by walking forward to the next user message*: shrinks
  the kept window too aggressively (drops valid recent context).
  Backward walk preserves more of what the user cares about.

**Failure mode**: if the user-message-boundary search reaches
`systemOffset` without finding a user message (i.e. the entire post-
system history is tool/assistant chatter with no user turns), the
reducer keeps everything post-system. Practically impossible given the
turn-append protocol, but guarded anyway.

### Decision 5: "Preserve prior summary block" rule in the observer prompt

**Chosen**: The observer system prompt includes a rule: "If the input
contains a prior `[session-summary]` block, preserve its sections
verbatim and update in place — do not rewrite or re-summarize."

The reducer's user-message-boundary walk-back is the structural defense:
when compaction runs on a session that already has a
`[session-summary session:{id}]` User-role message in its history, the
walk-back preserves that message in the kept window (it's a
non-system-nudge User message, which is a valid truncation point). The
observer then sees its own prior output in the discarded portion *only*
if the naive cut lands before the summary — in which case the
prompt-level rule kicks in: the model is instructed to preserve the
prior summary verbatim and update it in place.

Belt-and-suspenders. Cline does something similar for the focus-chain
checklist (`contextManagement.ts:46-50`): *"If no task_progress list
was included in the previous context, you should NOT create a new
task_progress list"* — structural and prompt-level defenses layered.

## Risks / Trade-offs

- **Risk**: The 9-section prompt is longer than the current free-form
  bullet prompt. Observer LLM call cost increases marginally. **Mitigation**:
  the observer uses `_compactionClient` which is typically a cheaper/
  weaker model (sidecar tier). The added cost is per-compaction, not
  per-turn. Measurable via existing usage telemetry on compaction boundary
  events.

- **Risk**: The structured output format is advisory, not enforced. If
  the model ignores the template and produces free-form text, the new
  "preserve prior summary" rule has no anchor. **Mitigation**: Cline has
  shipped the 9-section template for months on production Claude traffic
  without compliance issues. The section headers are distinctive enough
  that minor format drift is tolerable. If model compliance degrades on a
  new model family, we can tighten via forced tool-call response (also a
  Cline pattern — `SummarizeTaskHandler` uses the `summarize_task` tool).

- **Risk**: Header-based summary recognition relies on the observer LLM
  actually emitting the `[session-summary session:{id}]` header (the
  `WrapObservations` wrapper takes care of the canonical form even if
  the model omits it). **Mitigation**: `WrapObservations` is deterministic
  and normalizes any header-like first line to the canonical form. Tested.

- **Risk**: Reducer walking backward to a user-message boundary can
  *extend* the effective kept window past the requested `keepCount`.
  In a long tool-loop (many messages without an intervening user
  turn), the walk-back can carry the window all the way back to the
  user message that started the loop, effectively ignoring `keepCount`.
  **Mitigation**: today, we accept the larger window — extending is
  safer than orphaning tool pairs. The adaptive loop in
  `SessionCompactionPipeline.ExecuteAsync` halves `keepCount` on
  each iteration when estimated tokens exceed half the context window,
  but halving can't shrink the window below the walk-back floor. In
  pathological cases this produces no reduction and the next turn may
  re-trigger compaction immediately.
  **Future fix**: Cline's `ensureToolResultsFollowToolUse` pattern
  (`src/core/context/context-management/ContextManager.ts:375-477`)
  synthesizes placeholder Tool messages for orphan Assistant
  tool_calls and strips orphan tool_results, allowing truncation at
  ANY boundary without pair integrity violations. Worth adopting if
  production telemetry shows the walk-back failing to reduce context
  in real sessions. Out of scope for this change.

- **Defense in depth**: The reducer explicitly advances forward past
  leading Tool-role messages in the degenerate case where the walk-back
  falls through to `systemOffset` and that message is a Tool orphan
  (e.g. recovered from a prior bug that left orphan tools at the head
  of history). A kept window that starts with a Tool orphan would be
  rejected by downstream providers and trigger an infinite
  compact-retry loop — the skip-forward guards against that. See
  `ExtractiveSessionReducer.cs` and the
  `Degenerate_orphan_tool_at_head_is_advanced_past_not_kept` test.

- **Trade-off**: Journal grows. Pre-boundary messages stay in history
  for debugging/replay, so compacted sessions accumulate a growing
  tail of journaled-but-not-sent-to-LLM content. The memory cost is
  per-session; the storage cost is per-snapshot. Acceptable — it's
  what enables future checkpoint rollback and eval replay without
  re-running the session.

## Migration Plan

### Deployment

1. Ship as part of the `0.12` release cycle. No config toggle — the new
   behavior is always on.
2. No database migration. No new fields on `SessionCompacted`. Sessions
   compacted before this change continue to work: their existing
   `[observations from earlier in this session]` user message remains in
   history and the next compaction will fold it into the new
   `[session-summary session:{id}]` form via `WrapObservations`.

### Rollback

Revert the PR. No data migration needed — no persisted fields were
added or removed. Sessions compacted under the new format will have
User-role messages in history whose content starts with
`[session-summary session:{id}]`; the old code treats these as regular
user messages (harmless) and the next compaction on the reverted code
will fold them through the legacy observer prompt.

## Open Questions

- Should we eventually persist an explicit deleted-range index (like
  Cline's `conversationHistoryDeletedRange`) to enable checkpoint
  rollback and multi-tier summaries? Deferred — current rework uses
  header-based recognition which is sufficient for the primary goal
  (second-compaction defense). If rollback or multi-tier becomes a
  concrete feature, the index can be added additively without disturbing
  the header contract.
- Should the reducer's user-message-boundary walk have a maximum
  backtrack distance? If so, what happens when the walk exceeds it?
  Current decision: no limit. If the walk reaches `systemOffset`, keep
  everything. Revisit if production telemetry shows aberrant cases.
- Should the observer's "preserve prior summary" rule be enforced via
  a forced tool-call response (as Cline does), or rely on prompt
  compliance? Deferred — prompt compliance first, escalate to forced
  tool-call if production drift observed.
