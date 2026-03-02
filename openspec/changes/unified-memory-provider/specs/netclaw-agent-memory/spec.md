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
guided by the `identity-management` skill.

_Reconciliation: replaced conversational bootstrap with wizard-based bootstrap.
Removed `netclaw personality reset` CLI command (not planned for MVP)._

### ~~Requirement: Environment capability self-discovery~~

_Struck. Agents discover environment capabilities ad hoc via `shell_execute`.
No formal inventory is maintained._

### Requirement: Self-configuration through conversation

The system SHALL allow the agent to modify identity files (`SOUL.md`,
`AGENTS.md`, `TOOLING.md`) and skill files (`~/.netclaw/skills/*.md`) through
conversation using `file_read` and `file_write`. The `identity-management`
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

### Requirement: Unified memory provider abstraction

The system SHALL support pluggable memory backends through a provider
abstraction. Two providers SHALL be supported:

- **`files`** (default): File-based memory store at `~/.netclaw/memories/`
  with a `memory.md` progressive-discovery index.
- **`memorizer`**: Delegates to the Memorizer MCP server for full workspace,
  project, and relationship capabilities.

The active provider SHALL be configured in `netclaw.json` under a `Memory`
section. Default is `files` when no configuration exists.

#### Scenario: File-based memory is default

- **GIVEN** no `Memory` section exists in `netclaw.json`
- **WHEN** the daemon starts
- **THEN** the file-based memory provider is active
- **AND** `search_memories` and `store_memory` use the file backend

#### Scenario: Memorizer provider configured

- **GIVEN** `Memory.Provider` is set to `"memorizer"` in `netclaw.json`
- **AND** the Memorizer MCP server is configured and connected
- **WHEN** the daemon starts
- **THEN** `search_memories` delegates to `memorizer/search_memories`
- **AND** `store_memory` delegates to `memorizer/store`
- **AND** the memory context layer explains the two-step discovery process
  for advanced operations (workspaces, projects, relationships)

#### Scenario: Memorizer configured but disconnected

- **GIVEN** `Memory.Provider` is set to `"memorizer"`
- **AND** the Memorizer MCP server is not connected
- **WHEN** the agent calls `search_memories` or `store_memory`
- **THEN** the tools return a clear error indicating Memorizer is unavailable
- **AND** the memory context layer suggests checking MCP configuration

### Requirement: Always-available memory tools

The system SHALL provide `search_memories` and `store_memory` as always-loaded
builtin tools regardless of which memory provider is active. These tools SHALL
be in `ChatOptions.Tools` on every LLM call — no discovery step required.

#### Scenario: search_memories always available

- **WHEN** any session starts
- **THEN** `search_memories` is in the tool list sent to the LLM
- **AND** the tool works against whichever backend is configured

#### Scenario: store_memory always available

- **WHEN** any session starts
- **THEN** `store_memory` is in the tool list sent to the LLM
- **AND** the agent can save memories without first calling `search_tools`

### Requirement: File-based memory store

The file-based memory provider SHALL store memories as individual Markdown
files in `~/.netclaw/memories/` with a `memory.md` index file that lists all
memories with their titles, tags, and file paths. The index SHALL be updated
on every store operation.

#### Scenario: Store memory creates file and updates index

- **GIVEN** the file-based memory provider is active
- **WHEN** the agent calls `store_memory` with title, content, and tags
- **THEN** a new `.md` file is created in `~/.netclaw/memories/`
- **AND** the `memory.md` index is updated with the new entry
- **AND** the memory is retrievable via `search_memories`

#### Scenario: Search memories uses substring matching

- **GIVEN** memories exist in `~/.netclaw/memories/`
- **WHEN** the agent calls `search_memories` with a query
- **THEN** the tool searches the `memory.md` index and file contents
- **AND** returns matching memories ranked by relevance (title match > tag
  match > content match)

#### Scenario: Memory index is progressive-discovery catalog

- **GIVEN** memories exist in `~/.netclaw/memories/`
- **WHEN** the agent reads `memory.md` via `file_read`
- **THEN** the file lists all memories with title, tags, and file path
- **AND** the agent can `file_read` individual memory files for full content

### Requirement: Memorizer discovery guidance

When the Memorizer provider is active, the memory context layer SHALL explain
the two-step discovery process for advanced operations. The always-on layer
SHALL reference the `memorizer-usage` skill file for full guidance on
workspaces, projects, and relationships.

#### Scenario: Context layer explains two-step discovery

- **GIVEN** the Memorizer provider is active and connected
- **WHEN** the session system prompt is assembled
- **THEN** the memory context layer includes:
  - `search_memories` and `store_memory` are directly available
  - Advanced operations (workspaces, projects, relationships) require
    calling `search_tools(Server="memorizer")` first to discover tools
  - Reference to `memorizer-usage` skill for full workflow guidance

#### Scenario: Agent traverses memory graph

- **GIVEN** the agent finds a memory with a `projectId`
- **WHEN** the agent wants to understand the project context
- **THEN** the agent calls `search_tools(Server="memorizer")` to discover
  project tools
- **AND** calls `memorizer/get_project_context` to explore the project
- **AND** discovers related memories through project membership

### Requirement: Pre-compaction memory extraction

The system SHALL fire a memory extraction LLM call after compaction to
identify durable knowledge worth saving. Extracted memories SHALL be persisted
through the active memory provider. When no provider is configured, extraction
SHALL be skipped gracefully.

#### Scenario: Extraction persists to file backend

- **GIVEN** the file-based memory provider is active
- **WHEN** compaction completes and memory extraction runs
- **THEN** extracted memories are saved as files in `~/.netclaw/memories/`
- **AND** the `memory.md` index is updated

#### Scenario: Extraction persists to Memorizer

- **GIVEN** the Memorizer provider is active and connected
- **WHEN** compaction completes and memory extraction runs
- **THEN** extracted memories are saved via `memorizer/store`

#### Scenario: Extraction skipped when no provider

- **GIVEN** no memory provider is configured
- **WHEN** compaction completes
- **THEN** memory extraction is skipped (NullMemoryExtractor)
- **AND** compaction proceeds without delay
