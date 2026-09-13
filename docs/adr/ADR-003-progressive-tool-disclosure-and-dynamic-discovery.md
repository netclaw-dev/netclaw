# ADR-003: Progressive Tool Disclosure

**Date:** 2026-03-01
**Status:** Accepted
**Context:** Multi-MCP tool routing for small/medium local models

## Decision

Netclaw adopts progressive disclosure for tools:

1. Each tool registration has an internal `Core` or `Deferred` exposure tier.
2. Existing registration methods select `Deferred`. Named core methods must select `Core`.
3. The core contains only the workspace, discovery, skill, and shell compatibility tools.
4. Deferred first-party and MCP tools load on demand through `search_tools` and `load_tool`.
5. The live model catalog applies the current audience and feature policy.
6. Operator metadata is system-generated as complete shadow catalogs:
   - `identity/tooling/shadow/tool-index.md`
   - `identity/tooling/shadow/mcp/<server>.md`
7. Agent file and shell policies deny access to the complete shadow catalogs.
8. Fuzzy matching remains discovery-only. Suggestion results do not implicitly
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

Disk-backed catalogs make the complete tool graph observable across daemon
restarts. Operators can inspect this metadata outside agent tool paths. The
model receives a separate live catalog which the current policy filters.

### Why discovery-only fuzzy matching

Fuzzy suggestions improve recall during search but can be unsafe for execution.
Keeping fuzzy results non-callable avoids accidental invocation of similarly
named tools and preserves explicit tool loading semantics.

## Implementation Shape

- `ToolRegistry` stores exposure metadata without changing `ToolRegistration`.
- `ToolRegistry.GenerateCompressedIndex()` emits:
  - directly callable core tools,
  - compact deferred first-party hints, and
  - MCP server summaries and discovery instructions.
- `search_tools` supports the progressive flow:
  - `search_tools(query: "servers")`
  - `search_tools(query: "all", server: "<server>")`
  - `search_tools(query: "<intent>", server: "<server>")`
- `McpShadowCatalogWriter` generates and refreshes shadow catalogs at daemon
  startup after MCP initialization.
- `ToolIndexContextLayer` builds the model catalog from the live registry.
- `ToolAccessPolicy` applies audience, feature, grant, and deny filters.
- The session exposure cache gives all deferred tools the same bounded lease.

## Consequences

### Positive

- Smaller always-on prompt surface for tooling.
- Better multi-server navigation for models that struggle with large tool sets.
- Auditable operator metadata in a predictable filesystem location.
- Hidden tool names stay absent from model catalogs, search, suggestions, and load errors.
- Fewer accidental calls from fuzzy lookup results.

### Tradeoffs

- Additional generated files to manage under `identity/tooling/shadow/`.
- Loaded tool state is transient and must be rebuilt after actor recovery.
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
