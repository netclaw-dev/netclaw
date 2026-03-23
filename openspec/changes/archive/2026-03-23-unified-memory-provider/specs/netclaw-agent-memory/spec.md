## RECONCILED Requirements

The following requirements are updated to reflect the actual implementation.
Stale terminology, removed features, and spec-vs-reality drift are corrected.

### Requirement: Layered system prompt assembly

The system SHALL assemble session context from ordered layers: `SOUL.md`,
`AGENTS.md`, `TOOLING.md`, dynamic context layers (tool index, skill index,
memory index), and session-specific context. Later layers SHALL augment earlier
layers. Identity files SHALL be loaded at session start and cached for the
session lifetime. Missing files SHALL be omitted without error.

_Reconciliation: renamed files from `PERSONALITY.md/INSTRUCTIONS.md/USER.md`
to `SOUL.md/AGENTS.md/TOOLING.md`. Removed project AGENTS.md overlay (not
implemented, not planned for MVP). Added dynamic context layers._

### Requirement: Personality bootstrap via onboarding wizard

The system SHALL bootstrap agent personality through the `netclaw init`
onboarding wizard. The wizard SHALL collect owner identity, write initial
`SOUL.md`, and configure the standard identity directory. The agent MAY
refine personality through conversation using `file_write` on identity files,
guided by the `netclaw-identity` skill.

_Reconciliation: replaced conversational bootstrap with wizard-based bootstrap.
Removed `netclaw personality reset` CLI command (not planned for MVP)._

### ~~Requirement: Environment capability self-discovery~~

_Struck. Agents discover environment capabilities ad hoc via `shell_execute`.
No formal inventory is maintained._

### Requirement: Self-configuration through conversation

The system SHALL allow the agent to modify identity files (`SOUL.md`,
`AGENTS.md`, `TOOLING.md`) and skill files (`~/.netclaw/skills/*.md`) through
conversation using `file_read` and `file_write`. The `netclaw-identity`
built-in skill SHALL provide triage guidance for what information goes where.
The agent SHALL NOT have tools that directly modify `netclaw.json`,
`secrets.json`, ACL, or security policy.

_Reconciliation: replaced dedicated identity tools with generic file tools +
skill-based guidance. Safety boundary is architectural (no config-editing
tools) rather than per-tool validation._

### Requirement: Standard configuration directory

The system SHALL use `~/.netclaw/` as the standard configuration directory
with the following structure: `identity/` (soul files), `config/` (netclaw.json,
secrets.json), `skills/` (procedural knowledge), `memories/` (file-based
memory store), `sessions/`, `logs/`, `projects/`, and `schedules/`. The
directory SHALL be created at startup if it does not exist.

_Reconciliation: renamed `soul/` to `identity/`, added `skills/` and
`memories/`, removed `environment/` (struck). Configurable base path deferred._

## NEW Requirements

### Requirement: Unified memory backend with 4-tool surface

The system SHALL support pluggable memory backends through per-backend tool
implementations (no shared `IMemoryProvider` abstraction). Two backends SHALL
be supported:

- **`files`** (default): `FileMemoryStore` at `~/.netclaw/memories/`
  with a `memory.md` progressive-discovery index.
- **`memorizer`**: Per-tool delegation to the Memorizer MCP server — direct
  MCP pass-through for `find_memories`, `get_memories`, `update_memory`; and
  subagent-backed curation for `store_memory`.

Both backends expose the same 4-tool interface: `find_memories`, `get_memories`,
`store_memory`, and `update_memory`. The active backend SHALL be configured in
`netclaw.json` under a `Memory` section. Default is `files`.

#### Scenario: File-based memory is default

- **GIVEN** no `Memory` section exists in `netclaw.json`
- **WHEN** the daemon starts
- **THEN** the file-based memory backend is active
- **AND** all 4 file-backed tools are registered as always-loaded builtins

#### Scenario: Memorizer backend configured and connected

- **GIVEN** `Memory.Provider` is set to `"memorizer"` in `netclaw.json`
- **AND** the Memorizer MCP server is configured and connected
- **WHEN** the daemon starts and MCP discovery completes
- **THEN** `ToolIndexUpdater` registers 4 Memorizer-backed tools
- **AND** `store_memory` spawns a `memory-curator` subagent for curation
- **AND** `find_memories`, `get_memories`, `update_memory` delegate directly
  to MCP tools

#### Scenario: Memorizer configured but disconnected

- **GIVEN** `Memory.Provider` is set to `"memorizer"`
- **AND** the Memorizer MCP server is not connected
- **WHEN** the daemon starts
- **THEN** no memory tools are registered
- **AND** the memory context layer shows a disconnected warning with
  troubleshooting guidance

### Requirement: Two-phase memory retrieval

Memory retrieval SHALL use a two-phase pattern to reduce token cost:
`find_memories` returns lightweight results (ID, title, relevance score, tags,
150-character snippet) and `get_memories` fetches full content by ID(s).

#### Scenario: Two-phase retrieval flow

- **WHEN** the frontline model calls `find_memories` with a query
- **THEN** it receives lightweight results with IDs and snippets
- **AND** can call `get_memories` with selected IDs to fetch full content

### Requirement: File-based memory store

The file-based memory backend SHALL store memories as individual Markdown
files in `~/.netclaw/memories/` with a `memory.md` index file. The index
SHALL be updated on every store operation. `FileMemoryStore` SHALL be
thread-safe via `SemaphoreSlim` with an in-memory cache.

#### Scenario: Store memory creates file and updates index

- **GIVEN** the file-based memory backend is active
- **WHEN** the agent calls `store_memory` with title, content, and tags
- **THEN** a new `.md` file is created with YAML front matter
- **AND** the `memory.md` index is updated with the new entry
- **AND** the memory is retrievable via `find_memories`

#### Scenario: Search uses multi-level scoring

- **GIVEN** memories exist in `~/.netclaw/memories/`
- **WHEN** the agent calls `find_memories` with a query
- **THEN** title matches score 3 points per term
- **AND** tag matches score 2 points per term
- **AND** content matches score 1 point per term
- **AND** results are returned sorted by normalized score

#### Scenario: Memory index is progressive-discovery catalog

- **GIVEN** memories exist in `~/.netclaw/memories/`
- **WHEN** the agent reads `memory.md` via `file_read`
- **THEN** the file lists all memories with title, tags, and file path
- **AND** the agent can `file_read` individual memory files for full content

#### Scenario: Update memory via edit or delete

- **GIVEN** the file-based memory backend is active
- **WHEN** the agent calls `update_memory` with edit parameters
- **THEN** `FileMemoryStore.EditAsync` performs find-and-replace in the file
- **OR** `FileMemoryStore.DeleteAsync` removes the file and updates the index

### Requirement: Memorizer discovery guidance

When the Memorizer backend is active, the memory context layer SHALL explain
that `store_memory` delegates to a curation subagent (10–30s latency) while
`find_memories`, `get_memories`, and `update_memory` are fast MCP
pass-throughs. The always-on layer SHALL reference the `memorizer-usage` skill
file for full guidance on workspaces, projects, and relationships.

#### Scenario: Context layer explains delegation model

- **GIVEN** the Memorizer backend is active and connected
- **WHEN** the session system prompt is assembled
- **THEN** the memory context layer includes:
  - All 4 tools are available
  - `store_memory` uses subagent curation (10–30s expected)
  - Other tools are fast MCP pass-throughs
  - Reference to `memorizer-usage` skill for advanced operations

### Requirement: Pre-compaction memory extraction

The system SHALL fire a memory extraction LLM call after compaction to
identify durable knowledge worth saving. Extracted memories SHALL be persisted
through the active memory backend via `IMemoryExtractor`. `FileMemoryExtractor`
persists to `FileMemoryStore`; `MemorizerMemoryExtractor` persists via
Memorizer MCP.

#### Scenario: Extraction persists to file backend

- **GIVEN** the file-based memory backend is active
- **WHEN** compaction completes and memory extraction runs
- **THEN** `FileMemoryExtractor` saves extracted memories as files
- **AND** tags them as `["extraction", "compaction"]`
- **AND** the `memory.md` index is updated

#### Scenario: Extraction persists to Memorizer

- **GIVEN** the Memorizer backend is active and connected
- **WHEN** compaction completes and memory extraction runs
- **THEN** `MemorizerMemoryExtractor` saves via `memorizer/store` MCP tool
- **AND** tags as `["extraction", "compaction"]`

#### Scenario: Extraction graceful no-op when disconnected

- **GIVEN** the Memorizer backend is configured but disconnected
- **WHEN** compaction completes
- **THEN** `MemorizerMemoryExtractor` skips extraction without error
- **AND** compaction proceeds normally
