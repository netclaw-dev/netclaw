# PRD-007: Agent Personality and Local Memory

## Status

- State: Draft for execution (new)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-002`

## Goal

Define the agent soul (personality, instructions, user preferences), local
memory system (project registry, environment inventory), capability
self-discovery, self-configuration, and first-party tool access. This is the
"brain" of Netclaw — everything that makes it a persistent, context-aware
agent rather than a stateless chat endpoint.

## Product Outcomes

1. Netclaw has a consistent personality across sessions and restarts.
2. Operator preferences and project knowledge persist on disk.
3. Agent knows what tools and capabilities are available in its environment.
4. Agent can modify its own config through conversation (within safety bounds).
5. Agent has access to useful tools (search, shell, GitHub) through policy gates.

## Agent Soul Architecture

### File-Based Personality (Soul Files)

Agent identity is stored as data (markdown files), not code. The agent
reconstructs its personality from files on every session start. This makes
identity hot-swappable and version-controllable without code changes.

```
~/.netclaw/
  soul/
    PERSONALITY.md     # Agent character, tone, values, boundaries
    INSTRUCTIONS.md    # Operating rules, behavioral guidelines
    USER.md            # Owner preferences, timezone, how to address them
  projects/
    registry.json      # Registered project configurations
  environment/
    inventory.json     # Discovered tools, credentials, capabilities
  schedules/
    tasks.json         # Persisted scheduled tasks (see PRD-008)
  config/
    netclaw.json       # Main configuration (provider, Slack, DB, etc.)
    acl.json           # Access control policy (operator-only, not self-modifiable)
```

### Layered System Prompt Assembly

Session context is assembled from layers (later layers augment earlier):

1. **PERSONALITY.md** — who the agent is (values, tone, boundaries)
2. **INSTRUCTIONS.md** — how the agent operates (rules, workflows)
3. **USER.md** — who it serves (owner name, preferences, timezone)
4. **Project AGENTS.md** — context overlay when working on a registered project
5. **Environment summary** — condensed capability inventory
6. **Session context** — conversation history, tool results, memory

### Conversational Personality Bootstrap

On first interaction (or when personality files don't exist), the agent runs
a personality bootstrap conversation:

1. Introduce itself and explain the setup process
2. Learn the owner's name, preferences, communication style
3. Scan environment for installed tools and capabilities
4. Write initial PERSONALITY.md, INSTRUCTIONS.md, USER.md
5. Confirm readiness

This can be re-triggered via `netclaw personality reset` (PRD-004).

## Local Memory System

### Project Registry (`projects/registry.json`)

```json
{
  "projects": [
    {
      "name": "netclaw",
      "path": "/home/user/repos/netclaw",
      "agents_md": "/home/user/repos/netclaw/AGENTS.md",
      "channels": ["C0123NETCLAW"],
      "capabilities": {
        "language": "csharp",
        "framework": "akka.net",
        "has_tests": true,
        "has_ci": true
      }
    }
  ]
}
```

Projects are registered through conversation or CLI. When a user asks about
a project, the agent loads its AGENTS.md as a context overlay.

### Environment Inventory (`environment/inventory.json`)

```json
{
  "last_scan": "2026-02-21T10:00:00Z",
  "host": {
    "hostname": "pi1",
    "os": "linux",
    "arch": "arm64"
  },
  "tools": {
    "git": { "available": true, "version": "2.43.0", "hosts": ["github.com"] },
    "gh": { "available": true, "version": "2.44.0" },
    "claude": { "available": false },
    "opencode": { "available": false },
    "dotnet": { "available": true, "version": "10.0.100" },
    "node": { "available": false }
  },
  "mcp_servers": {
    "memorizer": { "configured": true, "reachable": true }
  }
}
```

Environment discovery runs at startup and can be re-triggered by the agent
or via `netclaw environment scan` (PRD-004).

## Capability Self-Discovery

The agent SHALL maintain awareness of its environment by discovering:

- Installed CLIs: `claude`, `opencode`, `git`, `gh`, `dotnet`, `node`
- Git credential availability (for which remote hosts)
- .NET SDK version and installed workloads
- Registered projects and their on-disk validity
- MCP server reachability and available tool counts

Discovery is:
- **Automatic at startup** — initial scan on process start
- **Periodic** — optional heartbeat re-scan (post-MVP)
- **On-demand** — agent can re-scan through conversation or CLI

Results are persisted to `environment/inventory.json` and summarized in the
system prompt.

## Self-Configuration

The agent can modify its own configuration through conversation:

### Allowed Self-Modifications

- Update PERSONALITY.md, INSTRUCTIONS.md, USER.md
- Register/unregister projects in registry.json
- Update environment inventory
- Create/manage scheduled tasks (PRD-008)

### Prohibited Self-Modifications (SEC-008)

- ACL rules and security policy
- Exposure mode and network configuration
- Tool grant policies
- Provider credentials

### Safety Protocol

1. Agent proposes the change and explains what will be modified
2. Change is validated before writing (schema validation, path checks)
3. File is written atomically (write to temp, rename)
4. Agent reports the change was saved
5. Session reboot is required for context refresh (config is cached)

## First-Party Tool Access

### Tool Implementation Strategy

1. **Use existing .NET packages** where available and well-maintained
2. **Build thin REST API wrappers** where no good package exists
3. **Shell-out to established CLIs** where they already work well
4. **Avoid** packages that are abandoned, have known vulnerabilities, or
   require proprietary licenses

### MVP Tool Set

#### Web Search

- **Primary**: Brave Search API (free tier: 2,000 queries/month)
  - Structured JSON response, popular in agent ecosystem
  - Requires API key configured during onboarding
- **Alternative**: SearXNG (self-hosted meta-search, no API key needed)
  - For operators who want zero external API dependencies
  - Runs in Docker on homelab infrastructure
- **Implementation**: Thin `HttpClient` wrapper registered as MEAI tool

#### Web Fetch

- Retrieve and parse content from URLs
- HTML-to-text extraction for LLM consumption
- Configurable output truncation to prevent context flooding
- **Implementation**: `HttpClient` + HTML parsing library

#### Shell Execution

- Run commands in the Netclaw process user context
- Timeout and output limits per SEC-009
- No interactive commands (stdin closed)
- Working directory is registered project path or scratch dir
- **Implementation**: `System.Diagnostics.Process` wrapper

#### GitHub (via `gh` CLI)

- Issue creation, PR management, repo operations
- Uses authenticated `gh` CLI (credentials managed by operator)
- No direct GitHub API calls needed
- **Implementation**: Shell-out to `gh` with structured output parsing

### Tool Registration with MEAI

All tools are registered as `Microsoft.Extensions.AI` tool definitions:

- Tool metadata (name, description, parameters) defined at startup
- Available tools filtered by session policy grants
- Tool results are returned to the LLM as tool response messages
- Tool invocations are logged for audit (SEC-007)

## Pre-Compaction Memory Flush

Before context compaction (FR-015), the agent executes a silent agentic turn:

1. System detects context approaching compaction threshold
2. Agent is prompted: "Session nearing compaction. Save important context."
3. Agent writes durable memories:
   - Important findings to Memorizer (external memory tier)
   - Updated project notes to local files if needed
   - Current task state summary to memory
4. Compaction proceeds after flush completes

This directly counters context rot — the primary failure mode of long-running
LLM sessions.

## Non-Goals (MVP)

- Hybrid search (vector + keyword) for local memory
- Memory hygiene / auto-cleanup of stale entries
- Multiple personality profiles or persona switching
- Hot-reload of personality files (requires session reboot)

## Acceptance Criteria

1. Agent personality is consistent across sessions and restarts.
2. Personality bootstrap conversation produces valid soul files.
3. Project registry persists and loads correctly.
4. Environment scan discovers installed tools accurately.
5. Self-configuration changes are validated before write.
6. Agent cannot modify ACL or security files through conversation.
7. Web search returns results when Brave API key is configured.
8. Shell execution respects timeout and output limits.
9. GitHub operations work through `gh` CLI shell-out.
10. Pre-compaction flush saves durable memories before context reset.
11. Tool invocations are logged with audit records.

## Cross-References

- MVP scope: PRD-001 (FR-006, FR-010, FR-011, FR-013, FR-014, FR-015)
- Security bounds: PRD-002 (SEC-003, SEC-008, SEC-009)
- CLI operations: PRD-004 (CLI-008, CLI-009)
- Provider tool calling: PRD-005 (MP-010)
- External memory: PRD-006 (MCP-007)
- Scheduling: PRD-008
