## ADDED Requirements

### Requirement: Pluggable memory backend

The system SHALL support two memory backends selected via `Memory.Provider` in
`netclaw.json`: `"files"` (default) and `"memorizer"`. Both backends SHALL
expose the same `store_memory` and `search_memories` tool interface to the
frontline model. The frontline model SHALL NOT need to know which backend is
active.

#### Scenario: File-backed memory tools registered

- **GIVEN** `Memory.Provider` is `"files"` or absent
- **WHEN** the daemon starts
- **THEN** `store_memory` and `search_memories` tools backed by
  `FileMemoryStore` are registered

#### Scenario: Memorizer-backed memory tools registered

- **GIVEN** `Memory.Provider` is `"memorizer"`
- **AND** the Memorizer MCP server is connected
- **WHEN** the daemon starts and MCP discovery completes
- **THEN** `store_memory` and `search_memories` tools backed by
  `MemorizerStoreMemoryTool` and `MemorizerSearchMemoriesTool` are registered
- **AND** these tools delegate to curation/retrieval subagents internally

#### Scenario: Memorizer configured but not connected

- **GIVEN** `Memory.Provider` is `"memorizer"`
- **AND** the Memorizer MCP server is not reachable
- **WHEN** the daemon starts
- **THEN** no memory tools are registered
- **AND** the context layer shows a disconnected warning with troubleshooting

### Requirement: Memorizer-backed store_memory delegation

The `store_memory` tool (when Memorizer-backed) SHALL spawn a curation
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

### Requirement: Memorizer-backed search_memories delegation

The `search_memories` tool (when Memorizer-backed) SHALL spawn a retrieval
subagent that enriches search results with project context, related memories,
and workspace metadata via the Memorizer MCP tool suite.

#### Scenario: Search memories via retrieval subagent

- **GIVEN** the Memorizer-backed `search_memories` tool is registered
- **WHEN** the frontline model calls `search_memories` with a query
- **THEN** the tool spawns a `memory-retriever` subagent
- **AND** the subagent searches, fetches details, and curates results
- **AND** the tool returns formatted memory results

#### Scenario: Search memory unavailable when Memorizer tools missing

- **GIVEN** `Memory.Provider` is `"memorizer"` but no Memorizer MCP tools are
  in the registry
- **WHEN** the frontline model calls `search_memories`
- **THEN** the tool returns "Memory search unavailable: Memorizer MCP server
  not connected."

### Requirement: Memory context layer per backend

The memory context layer SHALL provide backend-specific guidance to the
frontline model. The `MemorizerConnected` variant SHALL mention subagent
delegation and expected latency. The `FileBacked` variant SHALL reference the
local memory index file.

#### Scenario: Memorizer context layer includes subagent note

- **GIVEN** `Memory.Provider` is `"memorizer"` and Memorizer is connected
- **WHEN** the context layer is assembled
- **THEN** it includes guidance to use `store_memory` and `search_memories`
- **AND** notes that these tools delegate to curation subagents
- **AND** mentions expected latency of 10–30 seconds

#### Scenario: File-backed context layer references local index

- **GIVEN** `Memory.Provider` is `"files"`
- **WHEN** the context layer is assembled
- **THEN** it includes guidance to use `store_memory` and `search_memories`
- **AND** references `~/.netclaw/memories/memory.md` as the index
- **AND** does not mention subagents or Memorizer
