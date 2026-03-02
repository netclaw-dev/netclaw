## Context

Netclaw's cross-session memory is currently all-or-nothing: either the operator
runs a Memorizer MCP server, or the agent falls back to flat identity files with
no search, tags, or organization. The `SearchMemoriesTool` hardcodes
`"memorizer/"` as the MCP tool prefix. There's no config schema for memory, no
wizard step, no doctor check, and no status output.

The `IMemoryExtractor` interface and compaction integration are fully plumbed but
wired to `NullMemoryExtractor` — extracted memories go nowhere because no real
implementation is registered.

## Goals / Non-Goals

**Goals:**
- Memory works out of the box with zero configuration (file-based default).
- `search_memories` and `store_memory` are always-loaded tools — no discovery
  step required for basic memory operations.
- Memorizer is an upgrade path with full workspace/project/relationship
  capabilities, not a prerequisite.
- The context layer is honest about what requires discovery and what doesn't.
- Pre-compaction memory extraction persists to the active backend.
- Wizard, doctor, and status treat memory as a first-class capability.

**Non-Goals:**
- Vector/embedding search for the file backend. Substring matching is sufficient
  for MVP.
- Migrating memories between backends. Operator can do this manually.
- File-based workspaces/projects/relationships. These are Memorizer-only.
- Automatic Memorizer detection (if MCP server happens to be configured). The
  operator explicitly chooses the backend.

## Decisions

### Decision: Two always-loaded tools — search_memories and store_memory

These are the core memory operations. Making them always-loaded means the agent
can search and save without first calling `search_tools`. This solves the
discovery gap that caused small models to fail at saving memories.

Rationale:
- `search_memories` is already always-loaded. Adding `store_memory` is symmetric.
- Two tools cost ~200 tokens in ChatOptions — acceptable overhead.
- Eliminates the most common failure mode (agent told to save, can't find tool).

Alternatives considered:
- Promote all Memorizer tools to always-loaded. Rejected — 21 tools is too many
  tokens for every turn, and most are rarely used.
- Keep `store_memory` as MCP-only. Rejected — defeats the purpose of the
  behavioral SAVE trigger.

### Decision: File-based backend with memory.md index

Memories are stored as individual `.md` files in `~/.netclaw/memories/`. A
`memory.md` index file lists all memories with titles, tags, and file paths.
This is the progressive-discovery pattern already used for tools and skills.

Rationale:
- Consistent with how skills work (compressed index + file_read for details).
- Human-readable and editable — operator can browse, edit, or delete memories
  using any text editor.
- No database dependency for the default backend.

Alternatives considered:
- SQLite-backed memory store. Rejected — adds dependency, not human-readable,
  overkill for MVP where memory count will be small.
- Single `memories.md` file. Rejected — doesn't scale, hard to manage individual
  memories.

### Decision: Explicit provider config, not auto-detection

The `Memory.Provider` config field explicitly selects the backend. We don't
auto-detect Memorizer from the MCP server list.

Rationale:
- Operator intent is clear. No surprise behavior if someone adds a Memorizer MCP
  server for other purposes.
- Config is the source of truth for what memory backend is active.
- Doctor/status can report clearly because the expected backend is declared.

### Decision: Memorizer context layer explains two-step discovery

For advanced Memorizer operations (workspaces, projects, relationships), the
context layer tells the agent it needs to discover tools first and points to the
`memorizer-usage` skill file. This is honest about the architecture instead of
pretending tools are directly callable.

Rationale:
- Small models failed when told to use `memorizer/store` directly. Being explicit
  about the two-step process gives the agent a chance to succeed.
- The skill file has the full workflow documentation for graph traversal.
- Basic operations (`search_memories`, `store_memory`) don't require discovery.

### Decision: Wire IMemoryExtractor to active backend

The existing `IMemoryExtractor` plumbing in `LlmSessionActor` gets a real
implementation that routes to the active memory provider. The DI registration
in `Program.cs` picks the right implementation based on config.

Rationale:
- All the compaction plumbing is done. This is just wiring.
- Extracted memories go to the same backend the agent uses proactively.
- File backend gets extraction too — not just Memorizer.

## Architecture

### Memory Provider Interface

```csharp
public interface IMemoryProvider
{
    Task<string> SearchAsync(string query, int limit = 5, CancellationToken ct = default);
    Task<string> StoreAsync(string title, string content, string[]? tags = null,
        CancellationToken ct = default);
}
```

### File-Based Implementation

```
~/.netclaw/memories/
  ├── memory.md              (progressive-discovery index)
  ├── 2026-03-02-k8s-fix.md  (individual memory)
  ├── 2026-03-02-port-change.md
  └── ...
```

`memory.md` format:
```markdown
# Memory Index

| Title | Tags | File |
|-------|------|------|
| K8s pod restart fix | troubleshooting, kubernetes | 2026-03-02-k8s-fix.md |
| Daemon port change | troubleshooting, netclaw | 2026-03-02-port-change.md |
```

Search algorithm: substring match against title, tags, and file content.
Return top N results sorted by match quality (title > tags > content).

### Memorizer Implementation

Delegates to MCP tools:
- `SearchAsync` → `memorizer/search_memories`
- `StoreAsync` → `memorizer/store`

Resolves MCP tools at call time via `ToolRegistry.GetByName()` (same pattern
as existing `SearchMemoriesTool`).

### IMemoryExtractor Implementation

```csharp
public sealed class ProviderMemoryExtractor : IMemoryExtractor
{
    private readonly IMemoryProvider _provider;

    public async Task PersistAsync(string sessionId, string extractedMemories,
        CancellationToken ct = default)
    {
        await _provider.StoreAsync(
            title: $"Session extraction — {sessionId}",
            content: extractedMemories,
            tags: ["extraction", "compaction"],
            ct: ct);
    }
}
```

### DI Registration

```csharp
// In Program.cs ConfigureServices
var memoryConfig = configuration.GetSection("Memory");
var provider = memoryConfig.GetValue<string>("Provider") ?? "files";

IMemoryProvider memoryProvider = provider.ToLowerInvariant() switch
{
    "memorizer" => new MemorizerMemoryProvider(toolRegistry),
    _ => new FileMemoryProvider(paths.MemoriesDirectory)
};

services.AddSingleton(memoryProvider);
services.AddSingleton<IMemoryExtractor>(new ProviderMemoryExtractor(memoryProvider));
```

### Context Layer Content

**File backend:**
```
[memories — cross-session knowledge]

RETRIEVE: At the start of each conversation, search_memories for topics
relevant to the user's first message. Check before answering from scratch.

SAVE: When you learn something worth remembering, store_memory immediately.
Write rich content with markdown, code blocks, and full context.

Browse all memories: file_read ~/.netclaw/memories/memory.md
```

**Memorizer backend:**
```
[memories — cross-session knowledge via Memorizer]

RETRIEVE: search_memories is always available. Use it at conversation start
and when asked about something you might have encountered before.

SAVE: store_memory is always available for basic saves. For rich organization
(workspaces, projects, relationships), first call search_tools(Server="memorizer")
to discover the full tool set, then use the discovered tools.

When you find a memory with a projectId, you can explore its project context
and related memories — discover tools first, then traverse the graph.

For full Memorizer workflow: file_read ~/.netclaw/skills/memorizer-usage.md
```

### Config Schema

```json
{
  "Memory": {
    "Provider": "files"
  }
}
```

Or for Memorizer:

```json
{
  "Memory": {
    "Provider": "memorizer"
  }
}
```

The Memorizer MCP server entry in `McpServers` is still required separately —
`Memory.Provider` just tells Netclaw which backend to use for the unified tools.

## Risks / Trade-offs

- [File search quality] Substring matching is naive compared to Memorizer's
  vector search. Acceptable for MVP where memory count is small (tens to low
  hundreds). If quality becomes an issue, SQLite FTS5 is a natural upgrade path.
- [Index file stalability] The `memory.md` index can get out of sync if files
  are manually added/deleted. Mitigation: rebuild index from files on startup.
- [Two config concepts for Memorizer] Operator configures both
  `Memory.Provider = "memorizer"` AND the MCP server entry. Potential confusion.
  Mitigation: wizard handles both together; doctor validates consistency.

## Migration Plan

1. Add `IMemoryProvider` interface and file-based implementation.
2. Add `StoreMemoryTool` as always-loaded builtin.
3. Refactor `SearchMemoriesTool` to use `IMemoryProvider`.
4. Add `MemorizerMemoryProvider` that delegates to MCP tools.
5. Add `ProviderMemoryExtractor` and wire into DI.
6. Update `MemoryIndexContextLayer` for provider-aware content.
7. Add `Memory` config section and parsing.
8. Add wizard step, doctor check, and status output.
9. Update `memorizer-usage` skill with two-step discovery guidance.
10. Update `netclaw-agent-memory` main spec with reconciled requirements.
