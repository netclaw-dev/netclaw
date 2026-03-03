# ADR-002: Unified Context Discovery with Observational Memory

**Date:** 2026-03-02
**Status:** Accepted
**Context:** Agent context management — skills, within-session memory, cross-session memory

## Decision

Netclaw manages five kinds of context for the agent: identity (who am I),
procedural (how do I do things), semantic (what do I know), episodic (what
happened before), and environmental (what can I do). This ADR covers the
addition of three capabilities:

1. **Skills** — file-based procedural knowledge loaded on demand
2. **Observational memory** — within-session compression via Observer pattern
3. **Cross-session memory** — Memorizer MCP integration for durable knowledge

Each follows the existing tool discovery pattern (registry + meta-tool + context
layer) independently. No shared abstractions across context kinds.

## Problem

Before this change, Netclaw's compaction system was purely extractive: when the
conversation exceeded the context window threshold, old messages were simply
dropped. The agent had no way to reference information from earlier in the
conversation once it was compacted away. Additionally, there was no mechanism
for procedural knowledge (skills) or cross-session recall.

The identity tools (`identity_read`, `identity_write`, `identity_list`) were
redundant — the agent already had `file_read` and `file_write` which work on
any path. The identity tools added three tool slots that consumed context window
budget without adding capability the agent didn't already have.

## Architecture

### Skills System

```
~/.netclaw/skills/
  ├── identity-management.md    (built-in, copied from embedded resources)
  ├── self-diagnostics.md       (built-in)
  └── user-created-skill.md     (user-created)

Startup:
  CopyBuiltInSkills() → SkillScanner.Scan() → SkillRegistry → SkillIndexContextLayer

System prompt injection:
  SkillIndexContextLayer → "[skills — read with file_read for full instructions]
    identity-management (/home/user/.netclaw/skills/identity-management.md)
      How to read and update Netclaw identity files ...
    memorizer-usage (/home/user/.netclaw/skills/memorizer-usage.md)
      How to use the Memorizer MCP server ...
    self-diagnostics (/home/user/.netclaw/skills/self-diagnostics.md)
      How to check Netclaw configuration ..."
```

Skills are `.md` files scanned at startup. The compressed index in the system
prompt lists each skill's name, file path, and description. The agent uses
`file_read` directly to load full skill content — no dedicated search tool
needed.

Built-in skills ship as embedded resources and are copied to `~/.netclaw/skills/`
on first run. User edits are preserved (existing files are not overwritten).

### Observational Memory (Within-Session)

The Observer replaces the "discard and forget" behavior of extractive compaction
with "compress and remember."

**Before (extractive only):**
```
[system prompt]
[user msg 1]     ← discarded forever
[assistant msg 1] ← discarded forever
[user msg 2]     ← kept
[assistant msg 2] ← kept
```

**After (Observer + extractive):**
```
[system prompt]
[observations from earlier in this session]    ← NEW: compressed context
  - Discussed deployment strategy for services
  - [!] User prefers Docker Compose over K8s
  - Used shell_execute to check containers — 3 running
  Current task: setting up monitoring
[user msg 2]     ← kept
[assistant msg 2] ← kept
```

**Algorithm:**

```
CompactionTriggered
  │
  ├─ Phase 1: ClearOldToolResults (unchanged — deterministic)
  │
  ├─ Phase 2: ExtractiveSessionReducer determines keep vs discard
  │   └─ Adaptive: halves keep count if estimated tokens > 50% window
  │
  ├─ Phase 2b: Observer (NEW)
  │   ├─ Collect messages that will be discarded (between system prompt and keep window)
  │   ├─ Call _compactionClient with ObservationPromptBuilder prompt
  │   ├─ LLM compresses N messages → concise bullet-point observations
  │   ├─ Prepend observation as User message to kept messages
  │   └─ On failure: return null → fall back to extractive-only (graceful degradation)
  │
  ├─ Persist(SessionCompacted) with observations in CompactedMessages
  │
  └─ DrainBufferOrReady / MemoryExtraction (unchanged)
```

Key properties:

- **Same actor, same behavior state.** No new actors or concurrent sessions.
  The Observer call is an `await` inside the existing `CommandAsync<CompactionTriggered>`
  handler. The actor is already in `Compacting` behavior which buffers user messages.

- **Uses `_compactionClient`.** This is the cheap/fast model configured via
  `CompactionModelId` in session config. Falls back to the main model if not
  configured separately. The same client already handles title generation and
  memory extraction.

- **Observations are User-role messages** with a `[observations from earlier in
  this session]` delimiter. This is deliberate — system messages have special
  semantics in most LLM APIs, and user-role messages with clear delimiters are
  universally supported.

- **Observations compound.** When a second compaction fires, the previous
  observations are in the "discard" window and get compressed into
  observations-of-observations. This is acceptable for MVP; a dedicated
  Reflector phase (which would merge observations more intelligently) is
  deferred.

- **30-second timeout.** Observer failure (timeout, model error, empty response)
  degrades gracefully to extractive-only. No crash, no data loss.

### Cross-Session Memory (Unified Memory Provider)

```
Agent ──find_memories──→ FileFindMemoriesTool ──→ FileMemoryStore (local .md files)
       get_memories───→ FileGetMemoriesTool ──→ FileMemoryStore
       store_memory───→ StoreMemoryTool ────→ FileMemoryStore
       update_memory──→ FileUpdateMemoryTool → FileMemoryStore

Agent ──find_memories──→ MemorizerFindMemoriesTool ──→ memorizer/search_memories (MCP)
       get_memories───→ MemorizerGetMemoriesTool ───→ memorizer/get_many (MCP)
       store_memory───→ MemorizerStoreMemoryTool ──→ memory-curator SubAgentActor (8 MCP tools)
       update_memory──→ MemorizerUpdateMemoryTool ─→ memorizer/edit | memorizer/archive_memory (MCP)
```

Two pluggable backends behind a unified 4-tool surface (`find_memories`,
`get_memories`, `store_memory`, `update_memory`). No shared `IMemoryProvider`
abstraction — each backend has 4 dedicated tool classes. Backend selection via
`Memory.Provider` in `netclaw.json` (`"files"` default, `"memorizer"` upgrade).

- **File backend:** `FileMemoryStore` manages `~/.netclaw/memories/` with
  individual `.md` files, YAML front matter, and `memory.md` index. Tools are
  always-loaded builtins registered at startup.
- **Memorizer backend:** `store_memory` spawns a `memory-curator` subagent via
  `SubAgentActor` for dedup/routing/linking. `find_memories`, `get_memories`,
  `update_memory` are direct MCP pass-throughs. Tools resolve MCP at call time.

Two-phase retrieval: `find_memories` returns lightweight results (ID, title,
score, snippet), then `get_memories` loads full content for selected IDs.

Three context layers provide the agent with memory, skill, and tool awareness:

| Layer | States | Content |
|-------|--------|---------|
| `MemoryIndexContextLayer` | `FileBacked` | 4-tool guidance, two-phase retrieval, quality bar for store, update/delete instructions |
| | `MemorizerConnected` | Same + subagent delegation note, latency warning for store |
| | `MemorizerDisconnected` | Troubleshooting guidance, fallback to identity files |
| `SkillIndexContextLayer` | — | Compressed index of available skills with file paths for `file_read` |

Pre-compaction memory flush is handled by `IMemoryExtractor` implementations:
`FileMemoryExtractor` saves to `FileMemoryStore` with `["extraction", "compaction"]`
tags; `MemorizerMemoryExtractor` saves via `memorizer/store` MCP (graceful no-op
when disconnected). The agent also saves proactively during conversation, guided
by the behavioral triggers in `MemoryIndexContextLayer`.

## Rationale

### Why Observer instead of RAG or summarization?

**Summarization** (LLM rewrites the whole conversation into a summary) loses
detail and is expensive. **RAG** (retrieve relevant chunks from a vector store)
requires embedding infrastructure and adds latency to every turn.

The Observer pattern from Mastra's research achieves 94.87% accuracy on
LongMemEval with 3-40x compression, using only text (no vector DB). It works
because:

- Observations are terse and date-stamped — the LLM can scan them quickly
- Priority markers (`[!]`) help the LLM focus on what matters
- The "current task" line maintains continuity across compaction boundaries
- Compression is done once at compaction time, not on every turn

### Why no shared IDiscoveryIndex abstraction?

Each context kind has different characteristics:
- Tools: many items, need dynamic loading, MCP namespace prefixes
- Skills: few items, return full file content, category-based
- Memory: external MCP service, may be unavailable

A shared abstraction would be lowest-common-denominator and add indirection
without reducing code. Each context kind implements the pattern independently:
registry + search tool + context layer.

### Why remove identity tools?

The agent already has `file_read` and `file_write` which work on any path. The
identity tools (`identity_read`, `identity_write`, `identity_list`) added three
tool definitions that consumed context window tokens without providing
capability the agent lacked. The `identity-management` built-in skill provides
better guidance (what goes where, how to edit safely) than tool descriptions
could.

### Why User-role for observations, not System-role?

System messages have special handling in many LLM APIs (e.g., Anthropic's system
prompt is a separate field). Injecting observations as system messages could
interfere with the actual system prompt. User-role messages with a clear
`[observations from earlier in this session]` delimiter are universally
supported and clearly distinguish observations from fresh user input.

## Consequences

- Compaction no longer loses information. Discarded messages are compressed into
  observations that the agent can reference for the remainder of the session.
- The compaction LLM call adds latency (typically 1-3 seconds with a fast model).
  This happens during compaction when the user is already waiting.
- Observations grow until the next compaction cycle compresses them further.
  Very long sessions may accumulate large observation blocks. The Reflector
  (deferred) would address this.
- Three identity tools are removed, freeing context window budget.
- The agent now has behavioral triggers (via `MemoryIndexContextLayer`) that
  drive proactive retrieval and saving, reducing reliance on user instructions
  to search or store knowledge.
- Memorizer integration is gracefully optional — all other capabilities work
  without it.

## What Is Deferred

- **Reflector phase** — intelligently merging accumulated observations. For MVP,
  observations-of-observations via the Observer is sufficient.
- **Auto-RAG pre-turn retrieval** — agent uses `find_memories` explicitly.
  Sub-agent retrieval may be added if the agent proves bad at searching.
- **Episodic context** — querying session logs for "what happened before."
