# netclaw-agent-memory Specification

Research: `docs/research/agent-patterns.md`,
`docs/research/dynamic-context-discovery.md` (§5 — deferred memory retrieval
decisions: keyword vs. vector search, embedding strategy, injection budgets)

## Purpose

Define agent personality (identity files), cross-session memory (file-backed and
Memorizer backends), self-configuration through conversation, pre-compaction
memory flush, and the standard configuration directory structure. This capability
makes Netclaw a persistent, context-aware agent rather than a stateless chat
endpoint.

## Requirements

### Requirement: Layered system prompt assembly

The system SHALL assemble session context from ordered layers: `SOUL.md`,
`AGENTS.md`, `TOOLING.md`, dynamic context layers (tool index, skill index,
memory index), and session-specific context. Later layers SHALL augment earlier
layers. Identity files SHALL be loaded at session start and cached for the
session lifetime. Missing files SHALL be omitted without error.

#### Scenario: Full layer assembly on session start

- **GIVEN** identity files exist at `~/.netclaw/identity/SOUL.md`,
  `~/.netclaw/identity/AGENTS.md`, and `~/.netclaw/identity/TOOLING.md`
- **WHEN** a new session starts
- **THEN** the system prompt includes content from all three identity files in
  layer order (soul, agents, tooling)
- **AND** dynamic context layers and session-specific context are appended

#### Scenario: Missing identity file does not prevent session start

- **GIVEN** one or more identity files do not exist on disk
- **WHEN** a new session starts
- **THEN** the system assembles the prompt from available layers
- **AND** the missing layer is omitted without error

### Requirement: Personality bootstrap via onboarding wizard

The system SHALL bootstrap agent personality through the `netclaw init`
onboarding wizard. The wizard SHALL collect owner identity, write initial
`SOUL.md`, and configure the standard identity directory. The agent MAY
refine personality through conversation using `file_write` on identity files,
guided by the `identity-management` skill.

### Requirement: Self-configuration through conversation

The system SHALL allow the agent to modify identity files (`SOUL.md`,
`AGENTS.md`, `TOOLING.md`) and skill files (`~/.netclaw/skills/*.md`) through
conversation using `file_read` and `file_write`. The `identity-management`
built-in skill SHALL provide triage guidance for what information goes where.
The agent SHALL NOT have tools that directly modify `netclaw.json`,
`secrets.json`, ACL, or security policy.

#### Scenario: Agent updates identity file

- **GIVEN** the user asks the agent to adjust its personality
- **WHEN** the agent proposes and the user confirms the change
- **THEN** the agent writes the updated file using `file_write`
- **AND** reports that the change was saved

#### Scenario: Agent attempts to modify ACL

- **GIVEN** the user asks the agent to update ACL rules through conversation
- **WHEN** the agent evaluates the request
- **THEN** the agent refuses the modification
- **AND** explains that ACL changes require CLI or direct file edit by the
  operator

### Requirement: Pre-compaction memory flush

The system SHALL trigger a memory extraction LLM call before context compaction
to save durable memories. Extracted memories SHALL be persisted through the
active memory backend via `IMemoryExtractor`. `FileMemoryExtractor` persists to
`FileMemoryStore`; `MemorizerMemoryExtractor` persists via Memorizer MCP. The
flush SHALL complete before compaction proceeds.

#### Scenario: Flush triggered before compaction

- **GIVEN** session context approaches the compaction threshold
- **WHEN** the system detects compaction is imminent
- **THEN** the system executes a memory extraction LLM call
- **AND** persists extracted memories to the active backend
- **AND** compaction proceeds only after extraction completes

#### Scenario: Extraction persists to file backend

- **GIVEN** the file-based memory backend is active
- **WHEN** extraction runs
- **THEN** `FileMemoryExtractor` saves extracted memories as files
- **AND** tags them as `["extraction", "compaction"]`

#### Scenario: Extraction persists to Memorizer

- **GIVEN** the Memorizer backend is active and connected
- **WHEN** extraction runs
- **THEN** `MemorizerMemoryExtractor` saves via `memorizer/store` MCP tool

#### Scenario: Extraction graceful no-op when disconnected

- **GIVEN** the Memorizer backend is configured but disconnected
- **WHEN** extraction runs
- **THEN** `MemorizerMemoryExtractor` skips extraction without error
- **AND** compaction proceeds normally

### Requirement: Standard configuration directory

The system SHALL use `~/.netclaw/` as the standard configuration directory
with the following structure: `identity/` (soul files), `config/` (netclaw.json,
secrets.json), `skills/` (procedural knowledge), `memories/` (file-based
memory store), `sessions/`, `logs/`, `projects/`, and `schedules/`. The
directory SHALL be created at startup if it does not exist.

#### Scenario: Directory created on first startup

- **GIVEN** the `~/.netclaw/` directory does not exist
- **WHEN** the Netclaw process starts
- **THEN** the system creates `~/.netclaw/` and all required subdirectories

#### Scenario: Existing directory preserved

- **GIVEN** `~/.netclaw/` already exists with files
- **WHEN** the Netclaw process starts
- **THEN** existing files are not overwritten or removed
- **AND** any missing subdirectories are created

### Requirement: Pluggable memory backend with 4-tool surface

The system SHALL support two memory backends selected via `Memory.Provider` in
`netclaw.json`: `"files"` (default) and `"memorizer"`. Both backends SHALL
expose the same 4-tool interface to the frontline model: `find_memories`,
`get_memories`, `store_memory`, and `update_memory`. The frontline model SHALL
NOT need to know which backend is active.

#### Scenario: File-backed memory tools registered

- **GIVEN** `Memory.Provider` is `"files"` or absent
- **WHEN** the daemon starts
- **THEN** `find_memories`, `get_memories`, `store_memory`, and `update_memory`
  tools backed by `FileMemoryStore` are registered as always-loaded builtins

#### Scenario: Memorizer-backed memory tools registered

- **GIVEN** `Memory.Provider` is `"memorizer"`
- **AND** the Memorizer MCP server is connected
- **WHEN** the daemon starts and MCP discovery completes
- **THEN** `ToolIndexUpdater` registers 4 Memorizer-backed tools
- **AND** `store_memory` spawns a `memory-curator` subagent for curation
- **AND** `find_memories`, `get_memories`, `update_memory` delegate directly
  to MCP tools

#### Scenario: Memorizer configured but not connected

- **GIVEN** `Memory.Provider` is `"memorizer"`
- **AND** the Memorizer MCP server is not reachable
- **WHEN** the daemon starts
- **THEN** no memory tools are registered
- **AND** the context layer shows a disconnected warning with troubleshooting

### Requirement: Two-phase memory retrieval

Memory retrieval SHALL use a two-phase pattern: `find_memories` returns
lightweight results (ID, title, score, tags, snippet) and `get_memories`
fetches full content by ID. This reduces token cost and lets the model select
which memories to read in full.

#### Scenario: Two-phase retrieval flow

- **WHEN** the frontline model calls `find_memories` with a query
- **THEN** it receives lightweight results with IDs and snippets
- **AND** can call `get_memories` with selected IDs to fetch full content

### Requirement: Memorizer-backed store_memory delegation

The `store_memory` tool (when Memorizer-backed) SHALL spawn a `memory-curator`
subagent that handles deduplication, workspace routing, relationship linking,
and classification via the Memorizer MCP tool suite. The tool SHALL resolve
its required Memorizer MCP tools from the `ToolRegistry` at execution time.

#### Scenario: Store memory via curation subagent

- **GIVEN** the Memorizer-backed `store_memory` tool is registered
- **WHEN** the frontline model calls `store_memory` with title, content, and
  tags
- **THEN** the tool spawns a `memory-curator` subagent
- **AND** the subagent searches for duplicates, stores the memory, and creates
  references to related memories
- **AND** the tool returns a confirmation message

#### Scenario: Store memory unavailable when Memorizer tools missing

- **GIVEN** `Memory.Provider` is `"memorizer"` but no Memorizer MCP tools are
  in the registry
- **WHEN** the frontline model calls `store_memory`
- **THEN** the tool returns "Memory store unavailable: Memorizer MCP server
  not connected."

### Requirement: Memorizer-backed find/get/update as MCP pass-throughs

The `find_memories`, `get_memories`, and `update_memory` tools (when
Memorizer-backed) SHALL resolve the corresponding MCP tools from the
`ToolRegistry` at call time and delegate directly — no subagent is spawned.
They SHALL return a graceful error when the Memorizer MCP server is
disconnected.

#### Scenario: Find memories via Memorizer MCP

- **GIVEN** the Memorizer-backed `find_memories` tool is registered
- **WHEN** the frontline model calls `find_memories` with a query
- **THEN** the tool delegates to `memorizer/search_memories` MCP tool
- **AND** returns formatted search results

#### Scenario: Get memories via Memorizer MCP

- **GIVEN** the Memorizer-backed `get_memories` tool is registered
- **WHEN** the frontline model calls `get_memories` with IDs
- **THEN** the tool delegates to `memorizer/get_many` MCP tool
- **AND** returns full memory content

#### Scenario: Update memory via Memorizer MCP

- **GIVEN** the Memorizer-backed `update_memory` tool is registered
- **WHEN** the frontline model calls `update_memory` with edit or delete params
- **THEN** the tool delegates to `memorizer/edit` or `memorizer/archive_memory`
- **AND** returns a confirmation message

#### Scenario: MCP pass-through unavailable when disconnected

- **GIVEN** `Memory.Provider` is `"memorizer"` but MCP tools are missing
- **WHEN** the frontline model calls `find_memories`, `get_memories`, or
  `update_memory`
- **THEN** the tool returns a connection error message

### Requirement: File-based memory store

The file-based memory backend SHALL store memories as individual Markdown
files in `~/.netclaw/memories/` with a `memory.md` index file. `FileMemoryStore`
SHALL be thread-safe via `SemaphoreSlim` with an in-memory cache.

#### Scenario: Store memory creates file and updates index

- **GIVEN** the file-based memory backend is active
- **WHEN** the agent calls `store_memory` with title, content, and tags
- **THEN** a new `.md` file is created with YAML front matter
- **AND** the `memory.md` index is updated with the new entry

#### Scenario: Search uses multi-level scoring

- **GIVEN** memories exist in `~/.netclaw/memories/`
- **WHEN** the agent calls `find_memories` with a query
- **THEN** title matches score 3 points, tag matches 2, content matches 1
- **AND** results are returned sorted by normalized score

#### Scenario: Update memory via edit or delete

- **GIVEN** the file-based memory backend is active
- **WHEN** the agent calls `update_memory`
- **THEN** `FileMemoryStore.EditAsync` performs find-and-replace (edit mode)
- **OR** `FileMemoryStore.DeleteAsync` removes the file and updates index
  (delete mode)

### Requirement: Memory context layer per backend

The memory context layer SHALL provide backend-specific guidance to the
frontline model via three states: `FileBacked`, `MemorizerConnected`, and
`MemorizerDisconnected`. The `MemorizerConnected` variant SHALL mention
`store_memory` subagent delegation and expected latency. The `FileBacked`
variant SHALL reference the local memory index file.

#### Scenario: Memorizer context layer includes subagent note

- **GIVEN** `Memory.Provider` is `"memorizer"` and Memorizer is connected
- **WHEN** the context layer is assembled
- **THEN** it includes guidance for the 4-tool surface
- **AND** notes that `store_memory` delegates to a curation subagent
- **AND** mentions expected latency of 10–30 seconds for store operations

#### Scenario: File-backed context layer references local index

- **GIVEN** `Memory.Provider` is `"files"`
- **WHEN** the context layer is assembled
- **THEN** it includes guidance for the 4-tool surface
- **AND** references `~/.netclaw/memories/memory.md` as the index
- **AND** does not mention subagents or Memorizer

### Requirement: Memorizer discovery guidance

When the Memorizer backend is active, the memory context layer SHALL reference
the `memorizer-usage` skill file for full guidance on advanced operations
(workspaces, projects, relationships).

#### Scenario: Context layer explains delegation model

- **GIVEN** the Memorizer backend is active and connected
- **WHEN** the session system prompt is assembled
- **THEN** the memory context layer includes:
  - All 4 tools are available
  - `store_memory` uses subagent curation (10–30s expected)
  - Other tools are fast MCP pass-throughs
  - Reference to `memorizer-usage` skill for advanced operations
