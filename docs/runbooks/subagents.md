# Subagents

Subagents are specialist workers that the main Netclaw agent can delegate tasks
to. Each subagent runs autonomously with its own system prompt, tool set, and
timeout — then returns a synthesized result to the main agent. The delegation
protects the main session's context window from token-heavy work (deep research,
broad exploration, long summarization) and lets the main agent stay focused on
conversation and coordination.

## How it works

### Discovery

On every LLM turn, the main agent's system prompt includes an
`[available-subagents]` section that enumerates every user-facing subagent along
with its description, tools, and timeout:

```
[available-subagents — use spawn_agent to delegate]

## research-assistant
Deep web research with search and citation
Tools: web_search, web_fetch, file_read, attach_file
Timeout: 120s

## code-analyst
Analyze code, run commands, and review files
Tools: file_read
Timeout: 120s

## summarizer
Summarize documents and content concisely
Tools: file_read
Timeout: 60s

## How to delegate
Call `spawn_agent(agent: "<name>", task: "<specific task>", context: "<optional background>")`.

- `task` is what the subagent should do — be concrete and bounded.
- `context` is optional per-invocation background (workspace details, the user's broader goal,
  facts the subagent would otherwise have to rediscover). Do NOT duplicate the agent's built-in
  instructions — use this for THIS invocation's situation.
- Subagents run autonomously with their own tools and return a synthesized result, not a transcript.
```

The main agent sees this on every turn, so it always knows what subagents are
available and how to specialize them per call.

### Invocation

The main agent calls the `spawn_agent` tool. The tool takes three arguments:

| Argument | Required | Description |
|---|---|---|
| `agent` | Yes | Name of a registered user-facing subagent. |
| `task` | Yes | Specific, bounded description of what the subagent should do. |
| `context` | No | Per-invocation background (workspace details, broader goal, facts the subagent would otherwise rediscover). |

Example without context:

```json
{
  "agent": "research-assistant",
  "task": "Find the latest .NET 10 breaking changes for Akka.NET compatibility"
}
```

Example with context (the parent session passes workspace state that the cold
subagent can't see):

```json
{
  "agent": "code-analyst",
  "task": "Summarize the session lifecycle in LlmSessionActor.cs",
  "context": "Workspace is the netclaw repo on branch feature/subagent-stats. The user is investigating an adoption gap and wants a high-level map of how sessions create and reap subagents."
}
```

When `context` is populated, it is prefixed onto the subagent's first user
message as a `Context:` block followed by a `Task:` block. The agent's system
prompt (loaded from disk) is **not** modified — it stays verbatim and
reproducible across invocations. When `context` is null or whitespace the
first user message is just the raw task, identical to the pre-context protocol.

### Execution

1. The `spawn_agent` tool resolves the named agent from the definition registry.
2. Tools listed in the agent's definition are resolved from the tool registry
   and filtered against `SubAgentToolPolicy` for user-facing agents.
3. A `SubAgentActor` is spawned as a **child of the session actor** (supervised,
   lifecycle-managed — stops when the session stops).
4. The subagent runs an autonomous LLM loop: call tools, process results, repeat.
5. After at most 10 tool iterations or the configured timeout, the subagent
   returns its final text response.
6. The main agent receives this response as the `spawn_agent` tool result.

Child creation is marshaled back onto the session actor thread, so supervision
stays within Akka's actor-thread rules. If the parent tool call is cancelled or
times out, the subagent is cancelled too.

### Observability

Subagent start/complete events are emitted as `SubAgentOutput` session events:

```
[subagent:start] research-assistant (4 tools)
[subagent:done]  research-assistant (success, 23.4s)
```

These appear in the headless CLI output and session logs. They are suppressed
in Slack.

Completion events are emitted for every finished subagent run, even when the
subagent returns no structured findings. In that case `FindingsCount` is `0`
and the memory-decision fields are empty because there was nothing to review.

Structured findings are conservative, parent-reviewed durable-memory candidates.
They should be emitted as explicit conclusion envelopes with review metadata,
not inferred from free-form work logs or tool transcripts.

## Defining subagents

Agent definitions live in `~/.netclaw/agents/`. Each agent is a **single
markdown file** with YAML frontmatter carrying the metadata and the body
carrying the system prompt verbatim — the same `SKILL.md` convention the Netclaw
skill system uses and the de facto format used by Claude Code and OpenCode.

### File structure

```
~/.netclaw/agents/
  research-assistant.md
  code-analyst.md
  summarizer.md
```

One file per agent. No JSON sidecar. The filename is a convenience for humans;
the authoritative agent name comes from the `name` field in the frontmatter.

### Frontmatter fields

```markdown
---
name: research-assistant
description: Deep web research with search and citation
tools: [web_search, web_fetch, file_read, attach_file]
modelRole: Compaction
timeoutSeconds: 120
visibility: user-facing
emitStructuredFindings: false
---

You are a research assistant. Your job is to help the user by searching
the web, gathering information from multiple sources, and synthesizing
findings into clear, well-organized summaries.

## Guidelines

- Search for information using web_search, then fetch relevant pages with web_fetch.
- Cross-reference multiple sources when possible.
- Always cite your sources with URLs.
- Use file_read for local reference material when needed.
- Use attach_file if the parent session should deliver an existing file.
- Be thorough but concise — focus on facts and actionable information.
```

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `name` | Yes | — | Unique identifier. Used in `spawn_agent(agent: "<name>")`. Duplicate names across files are rejected with a warning. |
| `description` | Yes | — | One-line description shown in the `[available-subagents]` discovery block. |
| `tools` | Yes | — | List of tool names the subagent can use. For user-facing agents, only `web_search`, `web_fetch`, `file_read`, and `attach_file` are allowed. |
| `modelRole` | No | `Compaction` | `Compaction` (cheaper/faster) or `Main` (full model). |
| `timeoutSeconds` | No | `60` | Wall-clock timeout in seconds. |
| `visibility` | No | `user-facing` | `user-facing` (visible to `spawn_agent`) or `internal` (platform-owned, hidden). Accepts both hyphenated and PascalCase. |
| `emitStructuredFindings` | No | `false` | When true, successful output becomes a memory-candidate finding for parent-session review. |

The body below the closing `---` is the subagent's system prompt — verbatim.
Write it as markdown: headers, lists, code blocks. No placeholder interpolation
or templating; the body is loaded and handed to the subagent's LLM exactly as
written.

### Loader behavior (fail loud)

At daemon startup, `FileSubAgentDefinitionLoader` scans `~/.netclaw/agents/*.md`
and logs a specific warning for every file it rejects. A rejection does not
stop the scan — other valid files in the same directory still load. Rejection
reasons:

- Missing or unparseable YAML frontmatter
- Missing required field (`name`, `description`, or `tools`)
- Empty body (system prompt)
- Empty `tools` list
- One or more tools not in the user-facing allowlist
- Duplicate `name` across files (the alphabetically-first file wins)

Non-`.md` files in the agents directory (`stray.json`, `README.txt`, etc.) are
ignored at the glob layer and never logged.

### Writing effective subagent prompts

- **Be specific about the output format.** The main agent receives the
  subagent's final text response — make sure it's structured and useful.
- **Reference tools by name.** The subagent only has the tools listed in its
  definition. Tell it which tools to use and when.
- **Set boundaries.** Tell the subagent what NOT to do (e.g., "do not modify
  code unless explicitly asked").
- **Keep it focused.** A subagent with a narrow, clear purpose works better
  than a generalist. Per-invocation specialization is what the `context`
  parameter on `spawn_agent` is for — don't bake transient details into the
  agent file.

### Available tool names

These are the built-in tool names you can reference in agent definitions:

| Tool | Description |
|------|-------------|
| `web_search` | Search the web (requires Brave API key) |
| `web_fetch` | Fetch and parse web page content |
| `file_read` | Read file contents |
| `attach_file` | Attach a file to the response |

User-facing file-defined agents cannot request `shell_execute`, `file_write`,
`search_tools`, or raw MCP tool names. Those remain available to
platform-owned/internal subagents.

## Built-in agents

Three agents are seeded during `netclaw init`. They are regular file-based
definitions — you can edit or delete them.

**research-assistant** — Deep web research with search and citation.
Tools: `web_search`, `web_fetch`, `file_read`, `attach_file`. Timeout: 120s.

**code-analyst** — Analyze code, run commands, and review files.
Tools: `file_read`. Timeout: 120s.

**summarizer** — Summarize documents and content concisely.
Tools: `file_read`. Timeout: 60s.

## Creating a custom agent

Create a single `.md` file in `~/.netclaw/agents/`:

```markdown
---
name: github-reviewer
description: Read local PR notes and summarize next steps for the parent session
tools: [file_read]
modelRole: Compaction
timeoutSeconds: 90
visibility: user-facing
---

You are a GitHub review assistant. Read local notes and summarize what the
parent session should do next.

## Guidelines

- Do not execute commands or modify files directly.
- Format output as markdown for readability.
- Cite file paths with line numbers when referencing specific content.
```

Restart the daemon. The agent loads at startup after MCP servers connect (so
MCP tool names are resolvable) and appears in the `[available-subagents]`
discovery block.

If a tool name in your frontmatter doesn't match any registered tool, or falls
outside the user-facing allowlist, the agent is skipped with a specific
warning in the daemon log naming both the file and the disallowed tool — look
there first when a new agent "doesn't show up."

## Limitations

- Subagents are **single-turn**: they receive a task, run their tool loop, and
  return a result. They do not maintain conversation history or support
  back-and-forth interaction with the user.
- Subagents have a **maximum of 10 tool iterations** before being forced to
  produce a text response.
- Subagents run on the **compaction model** by default (cheaper/faster). Set
  `modelRole: Main` in frontmatter if the task requires the full model's
  capabilities.
- Subagents **cannot write durable cross-session memory** directly. They can
  return structured findings to the parent session for policy evaluation.
- Findings envelopes are intended for durable conclusion candidates only.
  Work-log or transcript-shaped envelopes are rejected by the parent-session
  review path.
- There is no inter-subagent communication — each subagent is independent.
- There is no per-agent model selection yet. `modelRole` routes through the
  three-slot `NetclawChatClientProvider` role system, which currently resolves
  to a single configured model for most installs. Per-agent model selection
  is tracked in a follow-on issue pending a multi-model provider architecture.
