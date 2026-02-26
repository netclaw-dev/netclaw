## Context

Netclaw was originally scoped as a Slack chat assistant with ACL and
persistence. The product vision has expanded to an always-on autonomous
operations agent with local memory, tool access, scheduling, self-discovery,
and self-configuration. The core Akka.Agents framework (session actors,
persistence, compaction, pub/sub broadcasts) remains unchanged — this design
extends it with new actor types, tool integration, and file-based state.

### Current State

- `LlmSessionActor`: persistent actor keyed by `{channelId}/{threadTs}`,
  handles turn loop, persists events, emits broadcasts, compacts via
  `SummarizingChatReducer`
- `LlmAgentParentActor`: wraps `GenericChildPerEntityParent`, routes messages
  by entity key using `SessionMessageExtractor`
- `SerializableChatMessage`: framework-owned persistence type (protobuf-net)
- Slack Socket Mode adapter: planned but not yet implemented
- SQLite journal/snapshots via Akka.Persistence (in-memory for tests)
- Microsoft.Extensions.AI `IChatClient` for LLM interaction

### Constraints

- Single-process architecture on ARM64 (pi1)
- Owner-operated, single-user trust model
- No public HTTP endpoint required for MVP
- CI tests must not require live LLM providers
- Akka.Persistence types must remain framework-owned and serializable

## Goals / Non-Goals

**Goals:**

- Extend session actor with tool calling and layered system prompt
- Add file-based agent personality and local memory loaded at session start
- Add scheduled task execution via Akka timers and fresh session actors
- Add first-party tools (web search, web fetch, shell, GitHub) with policy gates
- Add MCP tool discovery and registration through MEAI pipeline
- Define transport-agnostic input adapter contract
- Maintain all existing session actor, persistence, and pub/sub guarantees

**Non-Goals:**

- Changing the Akka.Agents framework itself (session actor core is stable)
- Ambient channel monitoring (Phase 2)
- Webhook ingress (Phase 2)
- Browser automation or delegated coding
- Web UI implementation (Phase 5, spec/mockup only)
- Sub-agent model routing
- Vector/hybrid search for local memory

## Decisions

### D1: File-based local memory over database

**Decision**: Store agent soul files, project registry, environment inventory,
and scheduled tasks as files on disk under `~/.netclaw/`.

**Alternatives considered**:
- SQLite with FTS5 + vector: More powerful search, but unnecessary for the
  small amount of data (< 100KB). Adds dependency complexity.
- PostgreSQL tables alongside Akka.Persistence: Conflates operational state
  with agent state. Makes backup/restore harder.

**Rationale**: Files are human-readable, version-controllable, trivially
backed up, and sufficient for MVP scale. The agent reconstructs its context
from files on every session start (IronClaw pattern). Upgrade to SQLite or
PostgreSQL is straightforward if scale demands it.

### D2: Tools as MEAI tool definitions, not custom protocol

**Decision**: All tools (first-party and MCP) are registered as
`Microsoft.Extensions.AI` `AIFunction` definitions and invoked through the
standard MEAI tool calling pipeline.

**Alternatives considered**:
- Custom tool protocol with manual JSON dispatch: More control, but duplicates
  what MEAI already provides and won't benefit from MEAI middleware.
- Separate tool actor per tool type: Adds actor overhead for what is
  fundamentally a function call with policy check.

**Rationale**: MEAI already handles tool definition, invocation, and response
serialization. The session actor doesn't need to know tool internals — it
just calls `IChatClient.GetResponseAsync()` and MEAI handles tool loops.
Policy checking happens at registration time (exclude ungrantable tools from
the session's tool set) and at invocation time (belt-and-suspenders).

### D3: ScheduleManagerActor as top-level actor, not nested under session parent

**Decision**: `ScheduleManagerActor` is a separate top-level actor that loads
task definitions from disk, manages Akka timers, and dispatches scheduled
executions by sending `SendUserMessage` commands to the session parent actor.

**Alternatives considered**:
- Schedule as a behavior of the session parent: Muddies the single
  responsibility of entity routing. Schedule is orthogonal to session identity.
- External cron daemon (systemd timers): Loses conversational management and
  requires IPC. Doesn't survive config changes through chat.

**Rationale**: Akka timers are the natural mechanism for periodic execution in
an actor system. A dedicated actor owns the schedule state (file on disk),
timer lifecycle, concurrent execution limits, and failure tracking. It
dispatches work to the session parent using the same command protocol as any
other input adapter.

### D4: Brave Search API as primary search with SearXNG alternative

**Decision**: Support two web search backends configurable in `netclaw.json`:
Brave Search API (requires API key, free tier 2000/month) and SearXNG
(self-hosted, no API key).

**Alternatives considered**:
- DuckDuckGo scraping: No official API, fragile, rate-limited
- Tavily: AI-focused but adds another paid dependency
- Google Custom Search: Complex setup, limited free tier

**Rationale**: Brave Search is the agent ecosystem standard (used by OpenClaw,
IronClaw). SearXNG provides a zero-dependency alternative for operators who
prefer self-hosting everything on homelab infrastructure. Both return
structured JSON suitable for LLM consumption.

### D5: Pre-compaction flush as system-initiated silent turn

**Decision**: When session context approaches the compaction threshold, the
system injects a system message prompting the model to save durable memories,
then proceeds with compaction after the flush turn completes.

**Alternatives considered**:
- Automatic extraction without model involvement: Can't determine what's
  "important" without model judgment.
- User-triggered save: Unreliable — users forget, and compaction can happen
  while they're away.

**Rationale**: The model is the best judge of what information should survive
compaction. OpenClaw's pre-compaction flush pattern is proven effective against
context rot. The flush turn is silent (not posted to Slack) and uses the same
turn loop as normal interaction.

### D6: Entity key patterns for multi-source routing

**Decision**: Different input sources use different entity key patterns:
- Slack: `{channelId}/{threadTs}` (existing)
- Scheduled tasks: `schedule/{taskId}/{runTs}`
- Future webhooks: `webhook/{source}/{eventId}`

**Alternatives considered**:
- Single flat key space: Risks collisions between Slack thread IDs and
  schedule IDs. No semantic routing information.
- Separate actor systems per source: Over-engineered for single-process MVP.

**Rationale**: Prefixed entity keys allow the session parent to route all
sources through the same `GenericChildPerEntityParent` mechanism while keeping
session isolation. The `SessionMessageExtractor` already handles key
extraction — it just needs to support multiple key formats.

### D7: Shell execution via Process wrapper, not Roslyn scripting

**Decision**: Shell tool executes commands via `System.Diagnostics.Process`
with timeout, output truncation, and stdin closure.

**Alternatives considered**:
- Roslyn C# scripting: More powerful but massive attack surface, hard to
  sandbox.
- Container-per-execution: Secure but slow and complex for MVP.

**Rationale**: Process execution is simple, well-understood, and matches how
other agent tools (Claude Code, OpenClaw) handle shell access. Security comes
from policy gates (must have `shell` grant) and boundaries (timeout, output
limits, no stdin). The Netclaw process user should have appropriate OS-level
permissions.

## Risks / Trade-offs

### [Risk] Agent self-modification creates inconsistent state
→ **Mitigation**: Validate all config changes before write. Atomic file writes
(temp + rename). Session reboot required for context refresh. ACL/security
files excluded from self-modification.

### [Risk] Shell execution is a high-risk attack surface
→ **Mitigation**: Policy-gated (requires explicit `shell` grant). Timeout
enforcement kills runaway processes. Output truncation prevents context
flooding. No interactive mode. Working directory restricted to project paths.

### [Risk] Scheduled task execution consumes resources on pi1
→ **Mitigation**: Max concurrent execution limit (default 3). Execution
timeout per task (default 5 minutes). Consecutive failure auto-pause.
Operator notification on failure.

### [Risk] Pre-compaction flush fails or takes too long
→ **Mitigation**: Flush has its own timeout. If flush fails, compaction
proceeds anyway (degraded but not blocked). Flush failure is logged.

### [Risk] File-based memory doesn't scale
→ **Mitigation**: MVP data volume is tiny (< 100KB). Migration path to SQLite
or PostgreSQL is straightforward — just change the storage backend behind the
same loading interface. Memorizer handles large-corpus knowledge.

### [Risk] Brave Search API key exposure
→ **Mitigation**: API keys stored in `config/netclaw.json` which is in the
data directory (not the repo). File permissions should be restrictive (600).
Keys are never logged or included in session context.

## Open Questions

1. **Slack thread reply format**: Should tool invocation results be shown to
   the user in Slack, or only the final agent response? Leaning toward final
   response only with tool activity available in diagnostics.

2. **Schedule persistence format**: JSON file is simple but doesn't support
   concurrent writes. Should we use a write-ahead pattern or just accept that
   schedule modifications are rare and serialize through the actor?

3. **Environment scan granularity**: How deep should capability discovery go?
   Current plan: binary availability + version. Should we also check
   authentication state (e.g., `gh auth status`)?
