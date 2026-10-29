# Subagents

Subagents are specialist workers that the main Netclaw agent can delegate tasks
to. Each subagent runs autonomously with its own system prompt, inherited
audience-scoped tool surface, and session-wide timeouts. It then returns a
synthesized result to the main agent. The delegation protects the main session's
context window from token-heavy work (deep research, broad exploration, long
summarization) and lets the main agent stay focused on conversation and
coordination.

## How it works

### Discovery

On every LLM turn, the main agent's system prompt includes an
`[available-subagents]` section that enumerates every user-facing subagent along
with its description and inherited tool policy:

```
[available-subagents — use spawn_agent to delegate]

## research-assistant
Deep web research with search and citation
Tools: inherited from parent audience policy, except denied sub-agent tools

## code-analyst
Analyze code, run commands, and review files
Tools: inherited from parent audience policy, except denied sub-agent tools

## summarizer
Summarize documents and content concisely
Tools: inherited from parent audience policy, except denied sub-agent tools

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
session shutdown stops the child. The timers are inactivity budgets: a
responsive subagent is not stopped merely because wall-clock time has elapsed.

Every subagent uses the same timers from `netclaw.json`. An agent file does not
set them:

| Timer | Setting | Default | Meaning |
|-------|---------|---------|---------|
| Prefill | `SubAgents.PrefillTimeoutSeconds` | `1800` | Max wait for the first streamed delta. Keepalives refresh it. A file may set `prefillTimeoutSeconds` to override it. |
| Inter-delta | `Session.FirstTokenTimeoutSeconds` | `600` | Max gap between streamed deltas after the first one. |
| No progress | `SubAgents.NoProgressTimeoutSeconds` | `1200` | Hard ceiling on time without real output. Text, tool calls, and thinking refresh it. Keepalives never refresh it. One model call that streams only thinking stops at three times this value (`thinking_only_timeout`). |

Agent files no longer set a per-agent timeout. A file that still contains
`timeoutSeconds` loads, and the loader logs one warning that names the file and
says the key is obsolete and ignored. Remove the key from the file.

Profiles that used the old `timeoutSeconds` default of `60` now use the
inter-delta budget of `600` seconds. The no-progress timer still bounds the run.

Two more rules end a run with outcome `failed`:

- `output_truncated`: a model reply ended on the output limit
  (`finishReason=length`) and made no tool call. The text is cut off, so it is
  never a result. The run does not retry. The failure text of the run result
  holds a redacted preview of at most 300 characters of the cut-off text. A
  reply that has a tool call still runs its tools.
- `thinking_only_timeout`: one model call streamed only thinking, with no text
  and no tool call, for longer than three times `SubAgents.NoProgressTimeoutSeconds`
  (default `1200`, so 60 minutes). Thinking counts as progress for the
  `no_substantive_output_timeout` deadline, so only this cap stops a model that
  thinks without end. A text delta with visible characters or a tool-call delta
  restarts the cap. A reasoning part never restarts it, also when its text is
  empty. The cap counts from the start of the call, so prefill time counts.

The main session applies the same two rules to its own model calls:

- A cut-off text reply with no tool call ends the turn with an error that says
  the reply was truncated. The session does not post the cut-off text and does
  not store it in history. A live viewer can see the streamed text before the
  error arrives.
- One call that streams only thinking ends the turn at three times
  `Session.NoProgressTimeoutSeconds` (default `1200`, so 60 minutes). The error
  says that the model streamed only thinking.
- Both errors have category `ModelBehavior`. Headless output reports
  `[turn:failed] category=model kind=ModelBehavior`. The provider worked, so do
  not look for an infrastructure fault.
- For a cut-off reply, ask for a shorter answer, or raise the model output limit.
- For the thinking cap, a larger output limit does not help. Ask for a simpler
  task, or use a model that thinks less. To allow longer thinking, raise
  `NoProgressTimeoutSeconds`: the cap is three times that value.

### Observability

Subagent start/complete events are emitted as `SubAgentOutput` session events:

```
[subagent:start] research-assistant (N tools)
[subagent:done]  research-assistant (success, 23.4s)
```

The start event reports the number of tools exposed for that parent audience and
profile. These events appear in the headless CLI output and session logs. They
are suppressed in Slack.

### Headless runs

`netclaw chat -p` waits for the child result. The run ends on the first
completed turn that reports no open child run. The session reports its open
runs in each `TurnCompleted`. A background shell job is not a child run.

- While the run waits, stdout shows `[subagent:waiting] <n> open: <run ids>`.
  With `--json`, stdout shows no such line.
- The child result starts a notice turn. The child text reaches stdout only
  when the model reads `result.md` with `file_read`.
- With `--json`, stdout holds one envelope. `response` is the text of the last
  turn. `turnResponses` holds each turn. `usage` is the sum over all turns.
- The run has no timeout of its own. Each child has its daemon timeouts. Use an
  outside `timeout` to bound the run.

A failed turn, SIGINT, SIGTERM, a stopped session, or a lost connection ends the run with exit `1`.
The CLI does not cancel the child runs. Stderr then names them:

```
[headless:open-runs] session=<sessionId> runs=<runId>[,<runId>...]
```

To read the result after such a stop, resume the session:

```bash
netclaw chat --resume <sessionId>
```

The session still owns the open runs. It starts a parent turn for each result.
A drained daemon restart during the wait ends the run with `kind=ChannelFailure`.
The runs stay open in the session, and recovery does not repeat their external work.

A lost connection also ends the run with `kind=ChannelFailure`, and a
`[headless:error]` line names it. The hub drops output while no client is
connected, and it does not replay that output after a reconnect. A turn that
completes during the gap is lost. So the CLI ends the run on `Disconnected` and
on `TransportClosed` at any time after it sent the prompt, whether or not it
knows an open child run. A short network drop therefore fails the run. Resume
the session with `netclaw chat --resume <sessionId>`. The CLI marks the prompt
as sent just before it sends the prompt. After such a failure, the daemon may
still run the prompt. Check the session, for example with
`netclaw chat --resume <sessionId>` or the session log, before you send the
prompt again. A daemon that stops without a drain sends no stop signal, so the
lost connection ends the run there.
Run `chat -p` under `timeout` so that an outside limit always bounds the run.

The CLI and the daemon must have the same version. A CLI from before this
behavior exits at turn 1 against a new daemon and loses the child result. Upgrade
the CLI with the daemon.

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
On Linux and macOS, the child `temp_dir` is
`/tmp/netclaw-<uid>/<key>/c<8 hex>`, in the key folder of the parent session.
The parent and its children can read each other's temp files. Another
session cannot. See
[Managed Temporary Folder](session-storage.md#managed-temporary-folder).
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
not count as subagent inactivity: the child parks and stops while it waits. If
no parent approval bridge is available (an unattended parent), the authorizer denies the gated tool with
`approval_required_unattended`. The tool does not run, and the subagent gets the
denial as a tool result (decision D2). If the bridge has no requester
authority context, the gated tool fails closed as a failed subagent run and is
not executed. A parked child survives a daemon restart, a crash, or passivation
of the parent session: its prompts stay live, and the child resumes after the
last answer. A child prompt shows only after the park is in the run record, so a
restart does not withdraw it. A prompt that an earlier version left open is
withdrawn at the upgrade restart: "This approval request was withdrawn because
Netclaw restarted before the task finished. Nothing ran."

## Built-in agents

Three agents are seeded during `netclaw init`. They are regular file-based
definitions — you can edit or delete them.

**research-assistant** — Deep web research with search and citation.
Tools are inherited from the parent audience/profile policy.

**code-analyst** — Analyze code and review files.
Tools are inherited from the parent audience/profile policy.

**summarizer** — Summarize documents and content concisely.
Tools are inherited from the parent audience/profile policy.

## Creating a custom agent

Create a single `.md` file in `~/.netclaw/agents/`:

```markdown
---
name: github-reviewer
description: Read local PR notes and summarize next steps for the parent session
modelRole: Compaction
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
  A repeated refused call does not stop the child at once. The runtime refuses
  the call again and adds a nudge. The child then selects another action or
  returns its result to the parent. A child cannot start a background job, so
  its nudge tells it to name a long command for the parent. The fifth refused
  repeat gets an escalated nudge. A sixth refused repeat ends the run as a
  successful `partial` result with the reason `tool_cycle_stopped` and the last
  text of the child.
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
A repeat after that correction gets the same correction and a nudge. The turn continues.
The runtime counts refused repeats in each turn. Refused repeat 5 gets an escalated nudge.
Refused repeat 6 ends the turn quietly with the text of the model. The person sees no error.
In a parent session, the turn completes with no `ModelBehavior` category. A child run ends with a successful partial result.
Unrelated calls remain available. Live context compaction preserves this evidence.
A process restart does not restore a detector checkpoint or repeat tool calls automatically.

Only a new turn and new person input reset the count. This includes person input that joins a running turn.
A child result in the middle of a turn does not reset it. An automation input that joins an automation turn does not reset it.
Examples of an automation input: a reminder fire, a background job result, and a webhook call.

A quiet end with no text posts nothing in Slack, Discord, and Mattermost. The session sets `TurnCompleted.QuietEnd`
(wire name `quietEnd`) for this end only. The channel then skips the empty-turn warning and still clears the typing indicator.
Any other turn with no reply keeps the warning.

A reminder turn that ends quietly records success with empty output, and it sends no failure alert.
The exception: a reminder that requires channel delivery, and whose loop never called the notify tool, records a failure in reminder history and the logs. It does not post in the thread.
To find such an end, search the session log for `turn_refused_repeat_end`. The line shows the number of refused repeats
and whether the last response had text.

Authorized status queries for a pending background job can repeat.
Each query repeats the normal access checks. Cancellation and inaccessible jobs receive no exception.

`MaxToolIterationsPerTurn` remains readable in old configuration files. The runtime does not enforce that value.
Useful work can exceed the former parent and child iteration limits.
The runtime still enforces authorization and the per-call tool timeouts.
The session and the TUI have no cancel command for a running turn.
A child has inactivity watchdogs only. Each model response resets them.
Distinct calls can continue indefinitely if the model, the provider, or a daemon stop does not end them.

A recurrence refusal executes no tool and grants no authority.
The result retains the call identity, validation error, and authorization-attempt identity.
The runtime does not request consent merely to refuse a repeated call.
Every call that can execute still passes the normal authorization checks.
