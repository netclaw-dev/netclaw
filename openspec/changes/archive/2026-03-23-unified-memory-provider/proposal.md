## Why

Netclaw's cross-session memory currently has two problems:

1. **No fallback.** If Memorizer (the MCP-based memory server) isn't configured,
   the agent's only option is flat identity files (`SOUL.md`, `TOOLING.md`).
   There's no searchable, taggable, organized memory — just files the agent
   can read and write. This means memory is effectively all-or-nothing: either
   you run a separate MCP server, or you get nothing.

2. **No abstraction.** Memory isn't a first-class Netclaw capability. It's an
   incidental MCP server entry named `"memorizer"` in `netclaw.json`. There's
   no config schema for memory, no wizard step, no doctor check, no status
   output. The `SearchMemoriesTool` and `MemoryIndexContextLayer` hardcode
   the string `"memorizer/"` to locate MCP tools.

3. **Discovery gap for advanced operations.** The always-on context layer tells
   the agent to "save using memorizer/store" but that tool isn't in
   `ChatOptions.Tools` until the agent first calls `search_tools`. Small models
   fail this two-step dance. Workspace/project/relationship operations are even
   harder to discover.

## What Changes

- **Unified memory tools**: `search_memories` (existing) and a new
  `store_memory` become always-loaded builtin tools that work regardless of
  backend. When Memorizer is connected, they delegate to MCP. When it's not,
  they use a file-based memory store.

- **File-based memory backend**: A `~/.netclaw/memories/` directory with
  individual `.md` files per memory and a `memory.md` index file that acts as
  a progressive-discovery catalog (titles, tags, paths). The agent uses
  `file_read` to load individual memories. The index is searchable via
  substring matching — no vector DB required.

- **Memory provider config**: A `Memory` section in `netclaw.json` that
  declares the active provider (`"files"` or `"memorizer"`). Default is
  `"files"` — Memorizer is opt-in when the MCP server is configured.

- **Updated context layer**: `MemoryIndexContextLayer` becomes
  provider-aware. For the file backend: points to `search_memories`,
  `store_memory`, and the `memory.md` index. For Memorizer: explains the
  two-step discovery process for advanced operations (workspaces, projects,
  relationships) and refers the agent to the `memorizer-usage` skill file.

- **Wizard step**: Add a memory configuration step to `netclaw init` that
  asks whether the user wants file-based memory (default) or Memorizer, and
  configures accordingly.

- **Doctor check**: Validate that the configured memory provider is healthy
  (files: directory exists and is writable; Memorizer: MCP server connected).

- **Status integration**: `netclaw status` shows memory provider and health
  alongside existing model/connector status.

## Capabilities

### New Capabilities

- None (memory falls under existing `netclaw-agent-memory` capability).

### Modified Capabilities

- `netclaw-agent-memory`: gains provider abstraction, file-based backend,
  `store_memory` builtin tool, and `memory.md` progressive-discovery index.
- `netclaw-onboarding`: gains memory provider selection step in wizard.
- `netclaw-cli`: gains memory provider doctor check and status output.

## Impact

- **Code/Runtime**: New `StoreMemoryTool`, `FileMemoryStore`, `memory.md`
  index writer. Modified `MemoryIndexContextLayer` for provider-aware content.
  Modified `SearchMemoriesTool` to support file backend. New wizard step and
  doctor check.
- **Security**: No new attack surface. File-based memories are local files
  under `~/.netclaw/` with existing filesystem permissions.
- **Operations**: Memory works out of the box with zero configuration.
  Memorizer becomes an upgrade path, not a prerequisite.
- **Dependencies/APIs**: No new external dependencies. File backend uses
  filesystem only.
- **Traceability**: Maps to `netclaw-agent-memory` spec (pre-compaction flush,
  memory triage, cross-session recall). Addresses gap where memory was
  unavailable without external MCP server.
