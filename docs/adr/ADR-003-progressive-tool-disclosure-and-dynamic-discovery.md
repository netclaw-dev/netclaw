# ADR-003: Progressive Tool Disclosure with Dynamic MCP Discovery

**Date:** 2026-03-01
**Status:** Accepted
**Context:** Multi-MCP tool routing for small/medium local models

## Decision

Netclaw adopts progressive disclosure for tools:

1. The always-on tool context contains only:
   - directly callable built-in tools, and
   - MCP server-level summaries (server name, purpose, tool count).
2. MCP tools are loaded on demand via `search_tools` and are not directly
   callable until discovered.
3. Tool metadata is system-generated to disk as shadow catalogs and used as the
   dynamic context source:
   - `identity/tooling/shadow/tool-index.md`
   - `identity/tooling/shadow/mcp/<server>.md`
4. Fuzzy matching remains discovery-only. Suggestion results do not implicitly
   load tools.

## Context

Netclaw already had dynamic MCP discovery, but prompt payloads were still too
front-loaded: broad MCP naming and workflow text appeared in every turn.
With smaller models (notably local Ollama models), this caused frequent tool
misfires, stalled responses, and poor server selection when multiple MCP
servers were enabled (browser + memorizer + others).

The product needs a workflow that is:

- context-window efficient,
- navigable by agents,
- inspectable by operators, and
- transport/model agnostic.

In practice, this means agents should first decide "which capability server do I
need" and only then load specific tools.

## Rationale

### Why progressive disclosure

Server-first discovery reduces prompt entropy. The model chooses among a small
set of capability summaries (browser, memory, email, etc.) before seeing full
tool detail. This lowers cognitive load and improves tool-selection reliability
without hard-coding domain routers in actor logic.

### Why file-backed shadow catalogs

Disk-backed catalogs make the tool graph observable and stable across daemon
restarts. Operators and agents can inspect generated metadata directly instead
of relying on transient in-memory state. This also creates a clear boundary:
human-authored identity content remains separate from system-authored tool
metadata.

### Why discovery-only fuzzy matching

Fuzzy suggestions improve recall during search but can be unsafe for execution.
Keeping fuzzy results non-callable avoids accidental invocation of similarly
named tools and preserves explicit tool loading semantics.

## Implementation Shape

- `ToolRegistry.GenerateCompressedIndex()` emits:
  - directly callable built-ins, then
  - MCP server summaries and explicit discovery instructions.
- `search_tools` supports the progressive flow:
  - `search_tools(query: "servers")`
  - `search_tools(query: "all", server: "<server>")`
  - `search_tools(query: "<intent>", server: "<server>")`
- `McpShadowCatalogWriter` generates and refreshes shadow catalogs at daemon
  startup after MCP initialization.
- `FileContextLayerProvider` injects `tool-index.md` into dynamic context
  layers on each LLM call.

## Consequences

### Positive

- Smaller always-on prompt surface for tooling.
- Better multi-server navigation for models that struggle with large tool sets.
- Auditable generated metadata in a predictable filesystem location.
- Fewer accidental calls from fuzzy lookup results.

### Tradeoffs

- Additional generated files to manage under `identity/tooling/shadow/`.
- Dynamic tool index freshness is tied to generation/update lifecycle.
- Tool accuracy still depends on model quality and memory retrieval quality; the
  disclosure strategy improves routing but does not solve semantic retrieval by
  itself.

## Alternatives Considered

1. **Keep full MCP detail in every prompt.**
   Rejected: too expensive in context tokens and brittle for smaller models.

2. **Use in-memory-only dynamic index.**
   Rejected: not inspectable, harder to debug, and opaque across restarts.

3. **Hard-code capability routing in actors.**
   Rejected: couples behavior to specific tool domains and reduces portability.

4. **Auto-load fuzzy matches as callable tools.**
   Rejected: increases accidental/incorrect tool execution risk.
