## ADDED Requirements

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
  tools backed by `FileMemoryStore` are registered

#### Scenario: Memorizer-backed memory tools registered

- **GIVEN** `Memory.Provider` is `"memorizer"`
- **AND** the Memorizer MCP server is connected
- **WHEN** the daemon starts and MCP discovery completes
- **THEN** `find_memories` (MCP pass-through), `get_memories` (MCP pass-through),
  `store_memory` (subagent-backed), and `update_memory` (MCP pass-through)
  tools are registered via `ToolIndexUpdater`

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
