# Subagents

Subagents are specialist workers that the main Netclaw agent can delegate tasks
to. Each subagent runs autonomously with its own system prompt, tool set, and
timeout — then returns a result to the main agent.

## How it works

### Discovery

On every LLM turn, the main agent's system prompt includes an
`[available-subagents]` section listing all user-facing subagents:

```
[available-subagents — use spawn_agent to delegate]
- research-assistant: Deep web research with search and citation (timeout: 120s)
- code-analyst: Analyze code, run commands, and review files (timeout: 120s)
- summarizer: Summarize documents and content concisely (timeout: 60s)

Use spawn_agent(agent: "<name>", task: "<description>") to delegate.
Subagents run autonomously with their own tools and return results.
```

The main agent sees this on every turn, so it always knows what subagents are
available and when delegation is appropriate.

### Invocation

The main agent calls the `spawn_agent` tool:

```json
{
  "agent": "research-assistant",
  "task": "Find the latest .NET 10 breaking changes for Akka.NET compatibility"
}
```

The task description becomes the subagent's user message. Be specific — the
subagent has no conversation history from the main session.

### Execution

1. The `spawn_agent` tool resolves the named agent from the definition registry.
2. Tools listed in the agent's definition are resolved from the tool registry.
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

Agent definitions live in `~/.netclaw/agents/`. Each agent is a JSON file with
an optional companion markdown file for the system prompt.

### File structure

```
~/.netclaw/agents/
  research-assistant.json    # agent definition
  research-assistant.md      # system prompt (referenced from JSON)
  code-analyst.json
  code-analyst.md
  summarizer.json
  summarizer.md
```

### JSON definition

```json
{
  "name": "research-assistant",
  "description": "Deep web research with search and citation",
  "systemPromptFile": "research-assistant.md",
  "tools": ["web_search", "web_fetch", "file_read", "attach_file"],
  "modelRole": "Compaction",
  "timeoutSeconds": 120
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `name` | Yes | Unique identifier. Used in `spawn_agent(agent: "<name>")`. |
| `description` | Yes | One-line description shown in the discovery context layer. |
| `systemPromptFile` | No | Path to a `.md` file (relative to `~/.netclaw/agents/`) containing the system prompt. Paths outside this directory are rejected. |
| `systemPrompt` | No | Inline system prompt string. Used if `systemPromptFile` is absent. |
| `tools` | Yes | List of tool names the subagent can use. For user-facing file-based agents, only `web_search`, `web_fetch`, `file_read`, and `attach_file` are allowed. |
| `modelRole` | No | `"Compaction"` (default, cheaper/faster) or `"Main"` (full model). |
| `timeoutSeconds` | No | Wall-clock timeout in seconds (default: 60). |

You must provide either `systemPromptFile` or `systemPrompt`. If both are
present, the file takes precedence.

### System prompt

The companion `.md` file is the subagent's system prompt — its personality,
instructions, and behavioral guidelines. This is what makes a subagent useful:

```markdown
You are a research assistant. Your job is to help the user by searching the
web, gathering information from multiple sources, and synthesizing findings
into clear, well-organized summaries.

## Guidelines

- Search for information using web_search, then fetch relevant pages with web_fetch.
- Cross-reference multiple sources when possible.
- Always cite your sources with URLs.
- Use file_read for local reference material when needed.
- Use attach_file if the parent session should deliver an existing file.
- Be thorough but concise — focus on facts and actionable information.
```

Tips for writing effective subagent prompts:

- **Be specific about the output format.** The main agent receives the subagent's
  final text response — make sure it's structured and useful.
- **Reference tools by name.** The subagent only has the tools listed in its
  definition. Tell it which tools to use and when.
- **Set boundaries.** Tell the subagent what NOT to do (e.g., "do not modify
  code unless explicitly asked").
- **Keep it focused.** A subagent with a narrow, clear purpose works better than
  a generalist.

### Available tool names

These are the built-in tool names you can reference in agent definitions:

| Tool | Description |
|------|-------------|
| `web_search` | Search the web (requires Brave API key) |
| `web_fetch` | Fetch and parse web page content |
| `file_read` | Read file contents |
| `attach_file` | Attach a file to the response |

User-facing file-defined agents cannot request `shell`, `file_write`, `search_tools`,
or raw MCP tool names. Those remain available to platform-owned/internal subagents.

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

1. Create a JSON file in `~/.netclaw/agents/`:

```json
{
  "name": "github-helper",
  "description": "Create issues, review PRs, and manage GitHub repos",
  "systemPromptFile": "github-helper.md",
  "tools": ["file_read"],
  "timeoutSeconds": 90
}
```

2. Create the companion prompt file:

```markdown
You are a GitHub review assistant. Read local notes and summarize what the
parent session should do next.

## Guidelines

- Do not execute commands or modify files directly.
- Format output as markdown for readability.
```

3. Restart the daemon. The agent will be loaded and appear in the discovery
   context layer.

Agents are loaded at daemon startup after MCP servers connect, so MCP tool names
are resolvable. If a tool name in your definition doesn't match any registered
tool, the agent is skipped with a warning in the logs.

## Limitations

- Subagents are **single-turn**: they receive a task, run their tool loop, and
  return a result. They do not maintain conversation history or support
  back-and-forth interaction with the user.
- Subagents have a **maximum of 10 tool iterations** before being forced to
  produce a text response.
- Subagents run on the **compaction model** by default (cheaper/faster). Set
  `"modelRole": "Main"` if the task requires the full model's capabilities.
- Subagents **cannot write durable cross-session memory** directly. They can
  return structured findings to the parent session for policy evaluation.
- Findings envelopes are intended for durable conclusion candidates only.
  Work-log or transcript-shaped envelopes are rejected by the parent-session
  review path.
- There is no inter-subagent communication — each subagent is independent.
