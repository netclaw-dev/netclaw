# Subagents

Subagents are specialist workers that the main Netclaw agent can delegate tasks
to. Each subagent runs autonomously with its own system prompt, inherited
audience-scoped tool surface, and timeout — then returns a synthesized result to
the main agent. The delegation protects the main session's context window from
token-heavy work (deep research, broad exploration, long summarization) and lets
the main agent stay focused on conversation and coordination.

## How it works

### Discovery

On every LLM turn, the main agent's system prompt includes an
`[available-subagents]` section that enumerates every user-facing subagent along
with its description, inherited tool policy, and timeout:

```
[available-subagents — use spawn_agent to delegate]

## research-assistant
Deep web research with search and citation
Tools: inherited from parent audience policy, except denied sub-agent tools
Timeout: 120s

## code-analyst
Analyze code, run commands, and review files
Tools: inherited from parent audience policy, except denied sub-agent tools
Timeout: 120s

## summarizer
Summarize documents and content concisely
Tools: inherited from parent audience policy, except denied sub-agent tools
Timeout: 60s

## How to delegate
Call `spawn_agent(agent: "<name>", task: "<specific task>", context: "<optional background>")`.

- `task` is what the subagent should do — be concrete and bounded.
- `context` is optional per-invocation background (workspace details, the user's broader goal,
  facts the subagent would otherwise have to rediscover). Do NOT duplicate the agent's built-in
  instructions — use this for THIS invocation's situation.
- Subagents run autonomously with audience-scoped tools and return a synthesized result, not a transcript.
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
2. The runtime starts from tools exposed by the parent session's audience/profile
   policy, then `SubAgentToolPolicy` removes tools that are statically denied to
   subagents (`spawn_agent`). Agent definition `tools:` metadata is advisory and
   does not narrow runtime authorization.
3. The session actor persists child acceptance and the accepted `spawn_agent`
   tool result before it starts the child actor.
4. The `spawn_agent` tool returns the durable run ID. The child runs as a
   supervised child of the session actor and can outlive the parent model call.
5. A final response completes the child run. An inactivity timeout or
   cancellation can stop it. The runtime sets no fixed tool-iteration ceiling.
6. Before the session accepts the start, the spawner writes `task.md` in the run
   folder (`subagents/<run_id>/`). It records the agent, the requester, and the
   brief. The spawner refuses a start when the requester cannot use `file_read`.
7. After the session records the terminal result, it reviews the child working
   context and writes `result.md` in the run folder. It then admits one notice
   as an ordinary input of the requester that owns the child. The notice names
   the agent, the run, the outcome, and the path of `result.md`. It holds no child
   output. The notice joins a running turn of the same requester, or it waits
   for its own turn, in admission order. The parent model reads the file with a
   real `file_read` call.

Child creation is marshaled back onto the session actor thread, so supervision
stays within Akka's actor-thread rules. After durable acceptance, the child can
continue after the `spawn_agent` call returns. An explicit run cancellation or
session shutdown stops the child. The timeout is an inactivity budget: a
responsive subagent is not stopped merely because wall-clock time has elapsed.

### Observability

Subagent start/complete events are emitted as `SubAgentOutput` session events:

```
[subagent:start] research-assistant (N tools)
[subagent:done]  research-assistant (success, 23.4s)
```

The start event reports the number of tools exposed for that parent audience and
profile. These events appear in the headless CLI output and session logs. They
are suppressed in Slack.

Completion events are emitted for every finished subagent run, even when the
subagent returns no structured findings. In that case `FindingsCount` is `0`
and the memory-decision fields are empty because there was nothing to review.
The completion event carries the same terminal outcome and reason used by the
tool-result envelope, so operators can distinguish a useful partial summary from
a failed run.

Structured findings are conservative, parent-reviewed durable-memory candidates.
They should be emitted as explicit conclusion envelopes with review metadata,
not inferred from free-form work logs or tool transcripts. They are not the
parent-facing `spawn_agent` result; they exist so accepted subagent conclusions
can enter the memory checkpoint pipeline without asking the parent model to parse
free-form work logs.

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

SkillServer feed sync can also install managed subagent definitions under
`~/.netclaw/agents/.server-feeds/<feed-name>/<agent-name>.md`. Those files are
owned by the server-feed sync process: edit local user-authored agents in the
top-level `~/.netclaw/agents/*.md` namespace instead. If a top-level local agent
and a managed feed agent declare the same `name`, the local definition wins and
the managed one is skipped with a warning.

### Frontmatter fields

```markdown
---
name: research-assistant
description: Deep web research with search and citation
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
- Return each authorized file path that the parent session should deliver.
- Be thorough but concise — focus on facts and actionable information.
```

| Field | Required | Default | Description |
|-------|----------|---------|-------------|
| `name` | Yes | — | Unique identifier. Used in `spawn_agent(agent: "<name>")`. Duplicate names across files are rejected with a warning. |
| `description` | Yes | — | One-line description shown in the `[available-subagents]` discovery block. |
| `tools` | No | `[]` | Advisory tool metadata retained for file-format compatibility. It does not constrain runtime tool access. |
| `modelRole` | No | `Compaction` | `Compaction` (cheaper/faster) or `Main` (full model). |
| `timeoutSeconds` | No | `60` | Inactivity timeout in seconds. The watchdog resets when the subagent makes progress. |
| `visibility` | No | `user-facing` | `user-facing` (visible to `spawn_agent`) or `internal` (platform-owned, hidden). Accepts both hyphenated and PascalCase. |
| `emitStructuredFindings` | No | `false` | When true, successful output becomes a memory-candidate finding for parent-session review. |

The body below the closing `---` is the subagent's system prompt — verbatim.
Write it as markdown: headers, lists, code blocks. No placeholder interpolation
or templating; the body is loaded and handed to the subagent's LLM exactly as
written.

### Loader behavior (fail loud)

On the next turn or subagent lookup, `FileSubAgentDefinitionLoader` rescans
top-level `~/.netclaw/agents/*.md` files first, then managed server-feed files
under `~/.netclaw/agents/.server-feeds/*/*.md`. It logs a specific warning for
every file it rejects. A rejection does not stop the scan — other valid files in
the same directory still load. Rejection reasons:

- Missing or unparseable YAML frontmatter
- Missing required field (`name` or `description`)
- Empty body (system prompt)
- Duplicate `name` across files (top-level local files win over managed feed files; managed feed duplicates use configured feed order)

Non-`.md` files in the agents directory (`stray.json`, `README.txt`, etc.) are
ignored at the glob layer and never logged.

### Writing effective subagent prompts

- **Be specific about the output format.** The main agent receives the
  subagent's final text response — make sure it's structured and useful.
- **Reference capabilities by name.** The subagent sees tools exposed by the
  parent audience/profile policy. Tell it which capabilities to use and when,
  but keep prompts robust when a tool is unavailable.
- **Set boundaries.** Tell the subagent what NOT to do (e.g., "do not modify
  code unless explicitly asked").
- **Keep it focused.** A subagent with a narrow, clear purpose works better
  than a generalist. Per-invocation specialization is what the `context`
  parameter on `spawn_agent` is for — don't bake transient details into the
  agent file.

### Tool access

Runtime tool access is derived from the parent session's audience/profile policy,
boundary, approval, and shell policies. The subagent denylist is then applied to
prevent recursive delegation through `spawn_agent`.

The `tools:` frontmatter field is advisory metadata only. It may be useful when
sharing definitions with other agent systems, but Netclaw does not use it as a
runtime whitelist.

Spawned subagents inherit the parent session's current `project_dir`.
Each child receives its own `temp_dir`, `artifact_dir`, and `log_path`.
The child also receives the session `session_dir` as its workspace base.
Project instructions come from the inherited project root.

A successful `spawn_agent` result returns the child run identifier.
It also returns the exact child log path and artifact directory.
The parent can inspect that log with `file_read`, `file_list`, or `file_search`.
The parent must not use a shell search to discover the log.

Example: The parent passes the returned log path to `file_read`.

Counterexample: The parent does not search a global log tree for the child.

If a subagent hits an approval-gated tool, the prompt is routed through the
parent session's approval channel and requester context. Human approval time does
not count as subagent inactivity or parent `spawn_agent` tool inactivity; both
watchdogs resume after the approval wait settles. If no parent approval bridge
is available (an unattended parent), the authorizer denies the gated tool with
`approval_required_unattended`. The tool does not run, and the subagent gets the
denial as a tool result (decision D2). If the bridge has no requester
authority context, the gated tool fails closed as a failed subagent run and is
not executed. Subagent approval waits are live-only: if the
daemon or parent session restarts before the user responds, the stale prompt is
expired and the interrupted parent `spawn_agent` call is closed before the next
turn continues.

## Built-in agents

Three agents are seeded during `netclaw init`. They are regular file-based
definitions — you can edit or delete them.

**research-assistant** — Deep web research with search and citation.
Timeout: 120s. Tools are inherited from the parent audience/profile policy.

**code-analyst** — Analyze code and review files.
Timeout: 120s. Tools are inherited from the parent audience/profile policy.

**summarizer** — Summarize documents and content concisely.
Timeout: 60s. Tools are inherited from the parent audience/profile policy.

## Creating a custom agent

Create a single `.md` file in `~/.netclaw/agents/`:

```markdown
---
name: github-reviewer
description: Read local PR notes and summarize next steps for the parent session
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

Save the file. The next turn or subagent lookup reloads the on-disk definitions
and refreshes the `[available-subagents]` discovery block.

If a spawned subagent cannot access a tool it expected, inspect the parent
session audience/profile policy and runtime tool discovery output. Frontmatter
`tools:` values do not grant or remove tool access.

If you edit a previously valid agent into an invalid state, the runtime drops it
from the active catalog on the next reload instead of serving the stale last
known-good version.

## Limitations

- Subagents are **single-turn**: they receive a task, run their tool loop, and
  return a result. They do not maintain conversation history or support
  back-and-forth interaction with the user.
- Subagents have no fixed maximum number of tool iterations. A final response
  completes the run; cancellation or an inactivity timeout can stop it.
  A recurrence refusal can settle the child turn without another model request.
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

## Coordination journal migration and restore

The compatibility reader accepts deployed child manifests `cra-v1`, `cre-v1`, and `tta-v1`.
It translates exact old child delivery correlations before the ordinary parent recovery path.
The current writer uses `cra-v2`, `cre-v2`, and `ss-v2` for child facts and snapshots.
The swap that adds child notices is roll-forward only. Build 0.27.1 reads the new rows without an error, and it reads them wrongly:

- It replays a notice as a plain user input.
- It prepares the result again for a run that the new build already reported and delivers it a second time, as a `child_run_result` pair.

Before the swap, stop the daemon and back up `netclaw.db`. To roll back, stop the daemon and restore that backup.
Never start 0.27.1 on a database that the new build wrote.

Unresolved historical evidence remains inert and visible through the owner status response.
Recovery does not relaunch uncertain child work. A result that the parent has not received becomes one notice.
A result that a deployed build already delivered as a `child_run_result` pair stays delivered, and no notice follows.
A notice carries only the recorded authority of the child owner.
After a crash, a notice can wait behind an older input of a person that has no turn (issue 2516).
Recovery then logs a Warning that names the run and says the notice waits behind an input.
The notice stays pending, and `result.md` stays in the run folder. Read it with `file_read`.

The restore unit is a complete, consistent pre-upgrade SQLite backup.
The backup must include the journal and snapshot tables from the same database state.
Retain its hash, source revision, and backup procedure receipt before an upgrade.
Restore a copied backup with the intended binary and prove actor recovery before an operational rollback.
A serializer round-trip alone does not prove database restore or runtime recovery.
A restore loses later journal facts and does not reverse external effects.

Use the fixed-byte compatibility and actor recovery checks in `Netclaw.Actors.Tests` for candidate validation.
Record the exact candidate, test filter, database copy, and results in the release evidence.
No live deployment or database restore forms part of this source change.

## Repeated tool work

The parent and child use exact tool identities and actual results to detect repeated work.
After two equal feedback rounds, the runtime returns a correction for each repeated call.
A repeat after that correction ends the turn without another model request.
Unrelated calls remain available. Live context compaction preserves this evidence.
A process restart does not restore a detector checkpoint or repeat tool calls automatically.

Authorized status queries for a pending background job can repeat.
Each query repeats the normal access checks. Cancellation and inaccessible jobs receive no exception.

`MaxToolIterationsPerTurn` remains readable in old configuration files. The runtime does not enforce that value.
Useful work can exceed the former parent and child iteration limits.
The runtime still enforces deadlines, cancellation, and authorization.
Distinct calls can continue indefinitely if no other stop condition applies.

A recurrence refusal executes no tool and grants no authority.
The result retains the call identity, validation error, and authorization-attempt identity.
The runtime does not request consent merely to refuse a repeated call.
Every call that can execute still passes the normal authorization checks.
