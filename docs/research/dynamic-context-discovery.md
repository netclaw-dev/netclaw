# Dynamic Context Discovery Research

Date: 2026-02-27
Task: M2.1–M2.3, M5.1–M5.2 (IMPLEMENTATION_PLAN.md)
Context: Before building MCP tool integration, research how production AI
agents handle large tool catalogs, memory retrieval, and skill/context loading
without bloating the context window. This research informs the architectural
decision on how Netclaw surfaces tools, memories, and future skill files to the
LLM agent.

---

## 1. The Problem: Context Window Bloat

### 1.1 Tool Definition Costs

Every tool exposed to an LLM costs tokens. A typical MCP tool definition with
name, description, and JSON Schema parameters runs 200–500 tokens. At scale:

| MCP Servers | Tools/Server | Total Tools | Estimated Tokens |
|-------------|-------------|-------------|-----------------|
| 1 | 6 | 6 | ~2K |
| 3 | 15 | 45 | ~15K |
| 5 | 20 | 100 | ~55K |
| 20 | 20 | 400 | ~100K+ |

A 5-server setup (GitHub, Slack, Sentry, Grafana, Memorizer) consumes ~55K
tokens in tool definitions before the model does any work. This is not
hypothetical — it is the documented reality from Cursor, Anthropic, and
multiple production systems.

Source: [Anthropic Tool Search Tool docs](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)

### 1.2 Tool Selection Accuracy Degradation

The problem is not just tokens — it is accuracy. Models get worse at picking
the right tool as the number of available tools grows:

| Study | Finding |
|-------|---------|
| Anthropic (internal) | Selection accuracy "degrades significantly" past 30–50 tools |
| RAG-MCP (2025) | Baseline accuracy dropped to 13.62% at scale (1→11,100 tools) |
| TaskBench (NeurIPS 2024) | Graph accuracy dropped from 96% (1 tool) to 25% (8-tool chains) |
| MCPVerse (2025) | "Most models suffered performance degradation" with larger tool sets |

The 30–50 tool threshold is consistent across studies. Beyond that, the model
is essentially guessing.

Sources:
- [RAG-MCP: Mitigating Prompt Bloat](https://arxiv.org/pdf/2505.03275)
- [TaskBench: Benchmarking LLMs for Task Automation](https://proceedings.neurips.cc/paper_files/paper/2024/file/085185ea97db31ae6dcac7497616fd3e-Paper-Datasets_and_Benchmarks_Track.pdf)
- [MCPVerse: Real-World Benchmark for Agentic Tool Use](https://arxiv.org/html/2508.16260v1)

### 1.3 The Problem Generalizes Beyond Tools

The same pressure applies to any large body of retrievable context:

- **Memories**: A knowledge base with hundreds of entries cannot be injected
  wholesale into every turn.
- **Skills/Instruction Files**: As the agent gains capabilities (project-specific
  rules, workflow instructions, domain knowledge), the volume of "potentially
  relevant" text grows unboundedly.
- **Reference Documents**: Project docs, API specs, runbooks.

All of these share the same shape: the agent needs *awareness* of what exists
and *access* to specific items on demand, without paying the token cost of
loading everything upfront.

---

## 2. Production Approaches

### 2.1 Anthropic: Server-Side Tool Search (defer_loading)

Anthropic's approach, used in Claude Code and the Claude API, is the most
mature production implementation.

**Mechanism:**

1. All tool definitions are sent in the API `tools` parameter, but marked with
   `defer_loading: true`.
2. The model sees only a `tool_search_tool` (regex or BM25 variant) plus 3–5
   non-deferred "always loaded" tools.
3. When the model needs a capability, it calls the search tool with a keyword
   or regex pattern.
4. The API returns 3–5 `tool_reference` blocks that get automatically expanded
   into full tool definitions in the model's context.
5. The model then calls the discovered tool normally.

**Key implementation details:**

- Two search variants: regex (`tool_search_tool_regex_20251119`) and BM25
  (`tool_search_tool_bm25_20251119`).
- Search indexes tool names, descriptions, argument names, and argument
  descriptions.
- Maximum 10,000 tools in catalog; 3–5 returned per search.
- Works with MCP via `mcp_toolset` with `default_config.defer_loading: true`.
- Custom client-side implementations supported — return `tool_reference`
  blocks from your own search tool.

**Results:**

- **85% token reduction** in tool definitions.
- Selection accuracy improved: Opus 4 from 49% → 74%, Opus 4.5 from 79.5% →
  88.1%.
- Available on Sonnet 4.0+, Opus 4.0+ (not Haiku).

**What Claude Code does specifically:**

Claude Code checks if MCP tool descriptions exceed 10K tokens. If so, tools
are deferred. The model receives a `ToolSearch` tool and short descriptions
listing available deferred tools. When the model needs a tool, it searches by
keyword, and 3–5 relevant tools (~3K tokens) get loaded per query.

Source: [Anthropic Tool Search Tool](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool),
[Advanced Tool Use](https://www.anthropic.com/engineering/advanced-tool-use)

### 2.2 Cursor: File-Based Dynamic Context Discovery

Cursor's approach treats files as the universal primitive for context
discovery, not just tools.

**Mechanism:**

1. MCP tool descriptions are synced to a filesystem folder structure (one
   folder per MCP server).
2. The agent receives only tool *names* in static context — not full schemas.
3. When the agent needs a tool, it uses standard file operations (`rg`, `jq`,
   `cat`) to read the full tool description from the synced folder.
4. Long tool responses are written to files; the agent uses `tail` to check
   endpoints and reads selectively.
5. Chat history during summarization is saved as files the agent can reference
   to recover lost context.

**Why files over search:**

Cursor explicitly rejected a flat search index approach. Their reasoning:
"We considered a tool search approach, but that would scatter tools across a
flat index." Files keep each server's tools logically grouped, enabling
cohesive understanding. The agent can browse by server, use grep with full
regex support, or filter with `jq`.

**Results:**

- **46.9% token reduction** in runs that called an MCP tool (A/B test,
  statistically significant, high variance based on number of MCPs installed).

**Key insight — the approach generalizes:**

Cursor applies the same file-based pattern to five categories:
1. Large tool outputs → written to files, selectively read
2. Chat history → saved as files for later reference
3. Domain-specific capabilities → stored as browsable files
4. MCP tool details → synced to folder, fetched on demand
5. Terminal output → synced to filesystem for grep-based searching

The unifying principle: "Files are a simple and powerful primitive" that models
already know how to navigate.

Source: [Cursor: Dynamic Context Discovery](https://cursor.com/blog/dynamic-context-discovery),
[InfoQ Coverage](https://www.infoq.com/news/2026/01/cursor-dynamic-context-discovery/)

### 2.3 Dynamic Context Loading (Three-Tier Progressive Disclosure)

A community pattern that formalizes progressive disclosure for MCP tools.

**Mechanism — three levels:**

| Level | What's in context | Token cost |
|-------|-------------------|------------|
| 1 — Server summaries | One-line per MCP server: "memorizer: persistent memory store" | ~50 tokens |
| 2 — Tool summaries | One-line per tool: "store: persist a text memory with tags" | ~200 tokens/server |
| 3 — Full definitions | Complete JSON Schema for selected tools | ~500 tokens/tool |

The model starts at Level 1. A "loader" meta-tool lets it drill down:
1. Agent sees server summaries → decides it needs memorizer tools
2. Calls loader("memorizer") → gets tool summaries for that server
3. Decides it needs `store` → calls loader("memorizer/store") → full schema
   loaded

**Key property:** The agent only pays the token cost of the tools it actually
uses. Everything else stays at summary level.

Source: [Dynamic Context Loading for LLMs & MCP](https://cefboud.com/posts/dynamic-context-loading-llm-mcp/)

### 2.4 Comparison Matrix

| Property | Anthropic (defer_loading) | Cursor (file-based) | DCL (three-tier) |
|----------|--------------------------|--------------------|--------------------|
| Token reduction | 85% | 46.9% | Not benchmarked |
| Search mechanism | Server-side regex/BM25 | File system ops (rg, jq) | Meta-tool calls |
| Provider-agnostic | No (Claude API only) | Yes | Yes |
| Works for non-tools | No (tool-specific API) | Yes (files for everything) | Adaptable |
| Requires file system | No | Yes | No |
| Implementation effort | Low (API flag) | Medium (sync infrastructure) | Medium (loader tool) |
| Agent awareness | Deferred tools invisible until searched | Names visible, details on demand | Server names visible, drill down |

---

## 3. Compressed Index Pattern (System Prompt Router)

### 3.1 The Vercel-Style Approach

Independently of the dynamic loading mechanisms above, there is a
complementary technique: injecting a **compressed index** into the system
prompt that gives the agent permanent awareness of available capabilities
without paying the full token cost.

This pattern is used in the dotnet-skills repository for routing agent
decisions to the correct skill files. Two formats exist:

**Readable format (~400 tokens):**

```markdown
## Agent Guidance: dotnet-skills

Routing (invoke by name)
- C# / code quality: modern-csharp-coding-standards, csharp-concurrency-patterns
- Testing: testcontainers-integration-tests, snapshot-testing

Quality gates (use when applicable)
- dotnet-slopwatch: after substantial new/refactor/LLM-authored code
```

**Compressed format (~200 tokens):**

```
[dotnet-skills]|IMPORTANT: Prefer retrieval-led reasoning over pretraining.
|flow:{skim repo patterns -> consult by name -> implement -> note conflicts}
|route:
|csharp:{modern-csharp-coding-standards,csharp-concurrency-patterns}
|testing:{testcontainers-integration-tests,snapshot-testing}
|quality-gates:{dotnet-slopwatch(after:substantial new/refactor/LLM code)}
```

The compressed format uses pipe delimiters to maximize information density.
The unusual formatting signals "this is machine-structured routing data" to
the model, potentially improving consistency in parsing.

Source: dotnet-skills `skills/skills-index-snippets/SKILL.md`,
Memorizer entry `da8d2175` (AGENTS.md dotnet-skills snippet)

### 3.2 Why the Compressed Index Matters

The index solves a problem that dynamic loading alone cannot: **the agent
doesn't know what it doesn't know.** Without an index, the agent has no reason
to search for a memorizer tool — it doesn't know memorizer exists. With the
index, the agent sees `mcp:memorizer:{store,search,get}` in every turn and can
reason about when to use it.

The index is a router, not documentation. It does not explain what `store`
does — it tells the agent "this exists, go look it up when you need it."

### 3.3 Generation

The dotnet-skills repository includes
`scripts/generate-skill-index-snippets.sh` which reads skill metadata from
`.claude-plugin/plugin.json` and generates the compressed index automatically.
The same pattern can generate tool indexes at runtime from `ToolRegistry`
contents.

---

## 4. How the Agent Knows When to Load

The hardest design question is not *how* to load dynamically, but *when* the
agent should decide to load. Four mechanisms exist:

### 4.1 Explicit System Prompt Instruction

The system prompt directly tells the agent to search before assuming it cannot
do something. Claude Code's `ToolSearch` tool description says: "You MUST use
this tool to load deferred tools BEFORE calling them."

This is brute force but effective. Combined with the compressed index
(Section 3), the agent has both the instruction to search and the awareness
of what to search for.

### 4.2 Task-Driven Reasoning

The agent pattern-matches the user's intent against the compressed index.
"Save this for next time" → matches `mcp:memorizer` → agent searches for
memory tools. This works when the user's request clearly implies a capability.

### 4.3 Failure and Retry

The agent tries to call a tool that is not loaded, gets an error, and retries
with a search. This is the worst mechanism — it wastes a turn and tokens on a
guaranteed failure. No system should design for this as the primary path, but
it serves as a fallback.

### 4.4 Automatic Pre-Turn Retrieval (System-Initiated)

For memories specifically, none of the above mechanisms work well. The agent
cannot reason about memories it does not know exist. Unlike tools (where the
task implies the capability) or skills (where the domain implies the
instruction), memories are opaque — the agent has no signal that relevant
prior knowledge exists.

The solution is system-initiated retrieval: before the LLM call, the system
performs a search (keyword or semantic) against the memory store using the
current conversation context, and injects relevant results as context. The
agent does not "decide" to load memories — the system pre-fetches them.

This is the standard RAG pattern applied to agent memory.

---

## 5. Memory Retrieval: Deferred Design Decisions

Memory retrieval strategy does not block MCP tool support (Milestone 2). The
agent can use Memorizer's `search` tool explicitly via MCP from day one. The
more sophisticated automatic retrieval is an optimization layered on after
memories exist to retrieve.

The following decisions should be revisited when implementing Milestone 5
(Agent Memory System).

### 5.1 Search Strategy: Keyword vs. Vector vs. Hybrid

| Strategy | Strengths | Weaknesses |
|----------|-----------|------------|
| Keyword (current Memorizer) | Fast, predictable, no infrastructure | Misses semantic relationships; agent must know what to search for |
| Vector/Embedding | Finds semantically related content the agent would not think to search for | Requires embedding infrastructure; similarity thresholds need tuning |
| Hybrid (keyword + vector) | Best of both — precise when the agent knows, serendipitous when it does not | Most complex; needs ranking/fusion strategy |

**Recommendation (deferred):** Start with keyword search via explicit MCP tool
calls (M2). When implementing automatic pre-turn retrieval (M5), add vector
search. Memorizer would embed memory content at `store` time and persist the
vector alongside the text. The pre-turn search becomes a cosine similarity
query.

### 5.2 What to Search Against

The quality of retrieval depends heavily on the query signal:

| Query Source | Signal Quality | Notes |
|-------------|---------------|-------|
| Raw user message | Low | "help me debug this actor" matches everything mentioning actors |
| Last N messages | Medium | Adds conversational continuity |
| Assembled turn context (system prompt + recent history + current message) | High | Topic + intent + continuity combined |
| Extracted keywords/entities from turn context | Highest (for keyword search) | Reduces noise; requires extraction step |

**Recommendation (deferred):** For vector search, embed against the assembled
turn context (last N messages + current message), not the raw user message
alone. For keyword search, extract entities/topics from the current turn to
form the query.

### 5.3 Injection Budget

Pre-turn memory injection must be bounded to avoid the same context bloat
problem it aims to solve.

**Recommendation (deferred):**
- Maximum 3–5 memories per turn (configurable).
- Minimum similarity threshold (e.g., 0.75 for vector search).
- Total injection budget: ~2K tokens for memories.
- Injected as a clearly delimited section in the system prompt or as a
  prefixed user message.
- The agent's explicit `memory_search` tool call has no budget limit — if the
  agent chooses to search, it gets full results.

### 5.4 When to Embed

Two options for when vector embeddings are created:

| Option | Pros | Cons |
|--------|------|------|
| At store time | No latency at retrieval; embedding is amortized | Requires re-embedding on memory update |
| At search time | Always fresh; no storage overhead | Adds latency to every turn; wasteful if same memory searched repeatedly |

**Recommendation (deferred):** Embed at store time. Memorizer already persists
the text — adding a vector column is incremental. Re-embed on update. This
keeps the pre-turn retrieval path fast (one similarity query, no embedding
call).

### 5.5 Embedding Provider

The embedding model should be configurable but default to a practical choice:

- If Ollama is configured: use a local embedding model (e.g.,
  `nomic-embed-text` or `mxbai-embed-large`).
- If a cloud provider is configured: use the provider's embedding API.
- Memorizer may also have its own embedding infrastructure — in that case,
  delegate to it entirely.

**Decision needed at M5 time:** Whether Netclaw or Memorizer owns the
embedding pipeline. If Memorizer already embeds content at store time, Netclaw
just calls `search` with a text query and Memorizer handles the vector
matching internally.

---

## 6. Proposal: Netclaw Dynamic Context Architecture

### 6.1 Two-Layer Design

```
┌─────────────────────────────────────────────────────┐
│                    System Prompt                     │
│                                                     │
│  Layer 1: Compressed Index (always present)          │
│  ┌─────────────────────────────────────────────┐    │
│  │ [tools]|route:                               │    │
│  │ |builtin:{shell,file_read,file_write}        │    │
│  │ |mcp:memorizer:{store,search,get,delete}     │    │
│  │ |mcp:github:{create_issue,search_repos,...}  │    │
│  │ |search:use search_tools for unlisted caps   │    │
│  └─────────────────────────────────────────────┘    │
│                  ~200–400 tokens                     │
└─────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────┐
│               ChatOptions.Tools                      │
│                                                     │
│  Layer 2: Active Tool Definitions                    │
│  ┌─────────────────────────────────────────────┐    │
│  │ Always loaded (full schema):                 │    │
│  │   shell_execute, file_read, file_write       │    │
│  │                                              │    │
│  │ search_tools meta-tool (full schema):        │    │
│  │   Searches ToolRegistry by keyword           │    │
│  │   Returns tool definitions for loading       │    │
│  │                                              │    │
│  │ Dynamically loaded (on demand):              │    │
│  │   [empty until agent calls search_tools]     │    │
│  └─────────────────────────────────────────────┘    │
│              ~2K tokens base, grows per search       │
└─────────────────────────────────────────────────────┘
```

### 6.2 Compressed Index Generation

At session start, the system generates the compressed index from `ToolRegistry`
contents:

1. Query `ToolRegistry` for all registered tools.
2. Group by source: `builtin` for first-party tools, `mcp:{server_name}` for
   MCP tools.
3. For each group, emit tool names only (no descriptions, no schemas).
4. Append routing instruction: "Use search_tools when you need a capability
   not listed above or need the full definition of a listed tool."
5. Inject into system prompt as a clearly delimited section.

### 6.3 The `search_tools` Meta-Tool

A first-party `INetclawTool` that searches the registry:

```
Name: search_tools
Description: Search for tools by keyword. Returns matching tool definitions
  that will be loaded for use. Use this when you need a capability from the
  tool index or need the full schema of a known tool.
Parameters:
  query: string — keyword or tool name to search for
  server: string? — optional MCP server name to scope search
Returns:
  List of matching tool definitions (name, description, parameter summary)
```

**Implementation:** Search tool names, descriptions, and parameter names using
case-insensitive substring matching. Return top 5 matches. The session actor
adds matched tools to the active `ChatOptions.Tools` for subsequent turns in
the same conversation.

**Provider-specific optimization:** When the backend is Claude (Anthropic),
use native `defer_loading: true` on the API call instead of the custom
meta-tool. The compressed index still serves as the awareness layer. This
gives the best of both worlds: native 85% token savings on Claude, and
provider-agnostic fallback for Ollama/OpenRouter.

### 6.4 Generalization to Non-Tool Context

The same two-layer pattern applies to memories, skills, and reference docs.
The index and search mechanism are shared; the *injection target* differs:

| Context Type | Index Entry Example | Search Tool | Injection Target |
|-------------|--------------------|----|------------------|
| MCP Tools | `mcp:memorizer:{store,search,get}` | `search_tools` | `ChatOptions.Tools` (callable) |
| Memories | `[memories]|topics:{slack-config,provider-setup,debugging}` | `search_memories` | Conversation messages (readable text) |
| Skills | `[skills]|available:{git-workflow,release-management}` | `search_skills` | Conversation messages (readable text) |

Tools are special because they must become callable `AITool` definitions.
Everything else is text injection into the conversation.

The shared infrastructure is:
- `IDiscoveryIndex` — generates compressed index entries for the system prompt
- `IContextSearch` — searches a registry by keyword, returns items
- Per-type specialization — tools go into `ChatOptions.Tools`; memories/skills
  go into messages

### 6.5 Automatic Memory Pre-Fetch (Future — M5)

For memories, supplement the explicit `search_memories` tool with automatic
pre-turn retrieval:

1. Before each LLM call, extract the current turn context.
2. Run a vector similarity search against embedded memories.
3. Inject top 3–5 results (above threshold) as a prefixed system message:
   "Relevant memories from prior sessions: ..."
4. The agent reads these passively — no tool call needed.

This is additive to the explicit search tool — the agent can still call
`search_memories` for targeted lookups. The pre-fetch catches things the agent
would not think to search for.

---

## 7. Implementation Sequencing

| Phase | What | Depends On |
|-------|------|-----------|
| **M2 (MCP)** | MCP client, `McpToolAdapter`, tool discovery, `search_tools` meta-tool, compressed index generation | Nothing |
| **M2 (MCP)** | Provider-specific optimization (Claude `defer_loading`) | M2 core |
| **M2.3 (Memorizer)** | Memorizer as MCP server, explicit `memorizer/search` tool | M2 core |
| **M5.1 (Memory extraction)** | `IMemoryExtractor` writes to Memorizer during compaction | M2.3 |
| **M5.2 (Memory retrieval)** | `search_memories` meta-tool, pre-turn vector retrieval | M5.1 + embedding infrastructure |
| **Future (Skills)** | `search_skills` meta-tool, skill file discovery | M2 pattern established |

### What to build now (M2):

1. MCP client (JSON-RPC over stdio/SSE)
2. `McpToolAdapter : INetclawTool`
3. `search_tools` meta-tool against `ToolRegistry`
4. Compressed index generation from `ToolRegistry` at session start
5. System prompt injection of compressed index
6. Keep 3–5 core tools always loaded; defer the rest

### What to defer:

- Vector embedding infrastructure (M5)
- Automatic pre-turn memory retrieval (M5)
- Skill file discovery and loading (post-MVP)
- Claude-specific `defer_loading` optimization (nice-to-have during M2)

---

## 8. Summary

| Area | Recommendation | Priority |
|------|---------------|----------|
| Tool loading | Two-layer: compressed index + search meta-tool | M2 (now) |
| Always-loaded tools | 3–5 core tools (shell, file ops) with full schemas | M2 (now) |
| Compressed index | Auto-generated from ToolRegistry, Vercel-style pipe format | M2 (now) |
| Provider optimization | Use Claude `defer_loading` when available | M2 (nice-to-have) |
| Memory search (explicit) | Agent calls `memorizer/search` via MCP | M2.3 |
| Memory search (automatic) | Pre-turn vector retrieval against turn context | M5 (deferred) |
| Embedding strategy | Embed at store time; search against assembled turn context | M5 (deferred) |
| Skill discovery | Same pattern as tools — index + search meta-tool | Post-MVP |
| Generalized interface | `IDiscoveryIndex` + `IContextSearch` shared infrastructure | M2 (design for it, build incrementally) |

---

## Sources

### Documentation
- [Anthropic: Tool Search Tool](https://platform.claude.com/docs/en/agents-and-tools/tool-use/tool-search-tool)
- [Anthropic: Advanced Tool Use](https://www.anthropic.com/engineering/advanced-tool-use)
- [MCP Specification: Tools](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)

### Industry Analysis
- [Cursor: Dynamic Context Discovery](https://cursor.com/blog/dynamic-context-discovery)
- [InfoQ: Cursor Dynamic Context Discovery](https://www.infoq.com/news/2026/01/cursor-dynamic-context-discovery/)
- [Dynamic Context Loading for LLMs & MCP](https://cefboud.com/posts/dynamic-context-loading-llm-mcp/)
- [MCP and Context Overload](https://eclipsesource.com/blogs/2026/01/22/mcp-context-overload/)
- [Tool RAG: Scalable AI Agents](https://next.redhat.com/2025/11/26/tool-rag-the-next-breakthrough-in-scalable-ai-agents/)

### Research Papers
- [RAG-MCP: Mitigating Prompt Bloat in LLM Tool Selection](https://arxiv.org/pdf/2505.03275)
- [TaskBench: Benchmarking LLMs for Task Automation (NeurIPS 2024)](https://proceedings.neurips.cc/paper_files/paper/2024/file/085185ea97db31ae6dcac7497616fd3e-Paper-Datasets_and_Benchmarks_Track.pdf)
- [MCPVerse: Real-World Benchmark for Agentic Tool Use](https://arxiv.org/html/2508.16260v1)

### Internal References
- dotnet-skills: `skills/skills-index-snippets/SKILL.md` (Vercel-style compressed index pattern)
- Memorizer entry `da8d2175`: AGENTS.md dotnet-skills snippet (router + quality gates)
- Netclaw PRD-006: MCP Tool Integration
- Netclaw `openspec/specs/netclaw-mcp/spec.md`
- Netclaw `openspec/specs/netclaw-agent-memory/spec.md`
- Spring AI: [Smart Tool Selection with Dynamic Tool Discovery](https://spring.io/blog/2025/12/11/spring-ai-tool-search-tools-tzolov/) (34–64% token savings)
