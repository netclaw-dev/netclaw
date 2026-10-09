# Subagents

Subagents are workers that the main Netclaw agent can use for independent tasks.
Each child has its own system prompt, inherited audience/profile tool policy, and operation health checks.
The owner accepts the task durably before the child completes it.
The parent can continue its work and accept later user input. A terminal child result arrives later.
Use [the engineering glossary](../spec/GLOSSARY.md) for shared terms.

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
3. The owner session commits the accepted run before it creates a `SubAgentActor` or returns acceptance.
4. The first tool response contains `run_id`, `scope_id`, `state`, and `control_tool` as strings.
5. The first state is `Accepted`. The control name is `check_agent_run`.
6. The child executes its model/tool loop with its original authority and a run-owned lifetime.
7. The owner records the terminal result before it acknowledges the child.
8. The owner admits one attributed continuation with a fresh tool-call/result pair after the original start batch settles.

Acceptance proves admission. It does not prove successful task completion or the current live state.
The tool output and model history contain the same canonical acceptance JSON.
Routed `skill_load` uses the same contract. Direct slash activation uses a human acknowledgement from the same owner facts.
Inline skills remain inline. A failed child route cannot silently execute inline.
Durable adoption preserves the fresh child result pair if its first parent review fails before the next turn record.
Owner recovery retains the pair without a child relaunch or duplicate result admission.

Child creation occurs on the owner actor thread.
Start-call completion, start-token cancellation after acceptance, and ordinary later parent input do not cancel the accepted child.
Operation health checks still apply. A healthy child can continue without a total task-age or tool-count limit.

### Status And Cancellation

The parent explicitly calls `load_tool` for `check_agent_run` when status or cancellation is necessary.
The control requires normal policy, the owning session, and the original eligible requester.
Children and foreign callers receive a denial without target details.
Run IDs and result paths grant no access authority.

Cancellation has four separate steps:

1. The owner commits cancellation admission.
2. The owner closes new model, tool, and approval-retry admission, then cancels active calls.
3. The owner confirms local closure after each admitted invocation returns its Task.
4. The framework retains a terminal result with confirmed partial evidence.

Cancellation admission alone does not prove dispatch closure.
An admitted invocation can delay closure before it returns its Task. The parent must still accept status requests.
Closure does not wait for that returned Task to complete. An external service can ignore its cancellation token.
After closure, a separate five-second deadline permits framework checkpoint retention and atomic run-local report writes.
The grace period permits no model request, task tool, project edit, external request, or approval action.
A failed report write or grace expiry preserves the last confirmed checkpoint and an explicit reason.
The deadline bounds the owner wait. It does not prove that an operating-system file operation stops.
Cancellation cannot undo an earlier effect. An external service can ignore cancellation and leave that effect's outcome unknown.

Cancelled completion retains `Success=false`, wire outcome `Failed`, and reason `CancelledByParent`.
Confirmed partial evidence does not turn cancellation into success.
The parent does not merge a working-context delta from a failed or cancelled child.
The first durable terminal or cancellation admission determines the terminal outcome.

### Approval Prompts And Recovery

A live child's approval prompt retains its original requester, exact call, authorization attempt, and accepted run.
The prompt can outlive the start call. Ordinary later parent input does not abandon it.
Child cancellation or loss expires it. A late answer creates no grant or retry.

Idle passivation defers live children and pending result admission.
Explicit stop and coordinated drain use the same child cancellation path.
Owner restart records unresolved accepted children as `Lost`. It does not resume or recreate them.
Committed cancellation retains its cancelled outcome. Committed terminal facts and pending delivery survive recovery.
The model chooses further work from the recorded evidence. The framework does not automatically replay uncertain effects.

Use cancellation and a revised child task when instructions must change.
Private agent messages, peer discovery, and live steering remain outside this release.

Assign one writer per workspace. Concurrent writers need separate authorized worktrees.
Preserve the operator's dirty checkout. Check child artifacts and verification evidence before the parent delivers them.
Only the parent sends user messages and attaches files under normal policy.

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
The completion event carries the recorded terminal outcome and reason.
Operators can distinguish a successful partial summary from a failed or cancelled run.

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

A successful `spawn_agent` result returns durable acceptance and the child run identifier.
The authorized `check_agent_run` response supplies the exact `log_path` and `artifact_directory` before terminal completion.
The parent can inspect that log with `file_read` or `file_search` under normal file policy.
Use bounded reads and targeted searches. Do not derive the path or search a global log tree.
A log shows diagnostic evidence. It does not prove current health, dispatch closure, or task completion.
Use the owner's recorded status and terminal result for lifecycle facts.

Example: The parent passes the returned log path to `file_read`.

Counterexample: The parent does not search a global log tree for the child.

Before reuse in eval fixtures or shared reports, scrub personal data and secrets from a derived transcript copy.
Preserve the original log unchanged. Use stable opaque replacements when correlation or equality matters.

The parent session routes a child's approval prompt with the original requester and exact call context.
Human approval time does not count as child inactivity. The child health check resumes after the approval wait settles.
The accepted run owns that wait after the start call completes.
A missing parent approval bridge or requester authority fails the run without tool execution.
Cancellation or loss expires the prompt. A late answer creates no grant or retry.
After owner restart, unresolved children become `Lost`; committed cancellation retains its cancelled outcome.
See [Approval Prompts And Recovery](#approval-prompts-and-recovery) for the lifecycle contract.

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
- Subagents have no static tool-round budget. The exact recurrence guard
  refuses a call after two equal completed feedback rounds. A repeated refusal
  ends the run with known partial results without another model response.
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

## Exact recurrence upgrade and rollback

Back up the session journal, snapshots, and job directory before activation.
Record the backup path and the backup time.
New `tta-v1` task events prevent a direct downgrade to the old binary.
Rollback requires a tested compatible reader or restoration of that recorded pre-upgrade backup.
Backup restoration loses journal state after the backup.
It cannot undo external effects from completed tools or jobs.
The plan documents this rollback limit; no rollback procedure receives proof without an executed test.

## Background Child Upgrade And Rollback

Stop ingress and complete the coordinated drain before you take a pre-upgrade backup.
Retain the session journal, snapshots, storage catalog, child logs, and run artifacts together.
Record the binary revision, backup path, and backup time. Test restoration against a copy.

New child events use registered manifests `cra-v1` and `cre-v1`.
An old binary cannot assume it can read these events. A protobuf field that an old reader ignores does not prove compatibility.
Direct binary rollback requires explicit reader proof. Otherwise, restore the recorded pre-upgrade backup before you start the prior binary.
Backup restoration loses journal state after the backup and cannot undo external effects.

After restart, inspect recorded `Lost` or cancelled runs and retained partial artifacts.
Do not recreate a child merely because its acceptance response exists.
The model decides whether to finish, revise the task, or request a new authorized attempt from the recorded session.
Reader and restoration test receipts establish only their stated storage scope. They do not prove a live operational rollback.
