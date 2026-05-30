---
name: subagent-authoring
description: "How to create and troubleshoot file-defined subagents in ~/.netclaw/agents. Load when the user asks to add, edit, or debug subagent definitions, or when a skill routes via metadata.subagent."
metadata:
  author: netclaw
  version: "1.2.4"
---

# Subagent Authoring

Use this skill when you need to create, update, or debug subagent definitions.

## Audience and Feature Gating

Subagents are subject to two independent gates:

- **Audience gate:** Public sessions cannot spawn subagents or see the
  subagent discovery context layer. `spawn_agent` returns a generic denial
  for Public.
- **Deployment gate:** `SubAgents.Enabled` in `netclaw.json` (default `true`).
  When `false`, `spawn_agent` is hidden from discovery for ALL audiences and
  the subagent discovery context layer returns empty.

Both gates must pass for subagent features to be available.

## When to use

Load this when the user asks to:
- create a new subagent
- edit advisory tool metadata, timeout, or behavior for an existing subagent
- diagnose why a subagent does not appear in `[available-subagents]`
- route a slash skill through `metadata.subagent`

## File format and location

Subagents are file-defined in:

`~/.netclaw/agents/*.md`

Each file is a single markdown document with YAML frontmatter and a markdown
body. The body is the subagent system prompt verbatim.

Minimal shape:

```markdown
---
name: my-agent
description: What this agent does
---

You are a specialist assistant. Your job is to...
```

With advisory tool metadata for compatibility with other agent systems:

```markdown
---
name: research-assistant
description: Deep web research with search and citation
tools: [web_search, web_fetch, file_read]
---

You are a research assistant.
```

## Required frontmatter fields

These are required for a file to load:
- `name` (string, non-empty)
- `description` (string, non-empty)

The markdown body below the closing `---` must also be non-empty.

## Optional frontmatter fields and defaults

| Field | Default | Notes |
|------|---------|-------|
| `tools` | `[]` | Advisory tool metadata retained for file-format compatibility. Netclaw does not use this as a runtime whitelist. |
| `modelRole` | `Compaction` | `Main` or `Compaction` (case-insensitive). Invalid values make the file fail loud and skip loading. |
| `timeoutSeconds` | `60` | Inactivity timeout for subagent execution. The watchdog resets when the subagent makes progress. |
| `visibility` | `user-facing` | Accepts `user-facing`, `UserFacing`, `internal`, or `Internal`. Invalid values make the file fail loud and skip loading. |
| `emitStructuredFindings` | `false` | When true, successful output is emitted as findings for parent-session review. |

Unknown fields are ignored.

## Example: valid subagent definition

```markdown
---
name: notion-planner
description: Summarizes local daily planning notes for the parent session
timeoutSeconds: 120
---

You are a planning assistant that reviews daily planning notes.

## Goal

Summarize the latest planning notes and highlight next actions.

## Guidelines

- Use file_read to inspect local planning notes and related reference files
- If a referenced file is missing, report that clearly
- Follow the user's existing plan format and structure
```

This agent inherits the parent session's audience/profile tool policy. User-facing
subagents then apply the static subagent denylist, which blocks recursive
delegation through `spawn_agent`. Frontmatter `tools:` values are advisory only.

## Runtime contract

Subagents run as headless workers on behalf of the parent session:
- do the delegated work as far as possible with the task, context, and tools
  exposed by the parent audience/profile policy
- do not ask the user clarifying questions or wait for conversational replies
- make reasonable assumptions when safe, and state them in the final output
- if blocked, return a final result describing what was found, what remains,
  and what decision the parent session needs

Parent-mediated tool approval is still allowed for concrete tool calls when the
parent channel supports it. Treat approval as a security gate, not as a dialogue
channel.

Subagents share the same tool-loop budget strategy as parent sessions: budget
nudges, duplicate-call nudges, and force-no-tools wrap-up. The current
subagent budget is 30 tool iterations per run, where one LLM response with any
number of parallel tool calls counts as one iteration.

## Fail-loud loader behavior

On the next turn or subagent lookup, invalid files are skipped with warnings.
Common rejection reasons:
- missing or unparseable YAML frontmatter
- missing required fields (`name`, `description`)
- empty markdown body
- duplicate `name` across files (first file in stable sorted order wins)

If you edit a previously valid file into an invalid state, the runtime drops it
from the active subagent catalog on the next reload instead of serving the stale
last-known-good definition.

## Inherited parent context

Spawned subagents inherit the parent session's `session_dir` and current
`project_dir` as read-only grounding. The child can use those paths for file
resolution and project instruction loading, but it does not mutate the parent
session's working context.

Non-`.md` files in `~/.netclaw/agents` are ignored.

## Relationship to skill routing (`metadata.subagent`)

Skills can route execution through a subagent with:

```yaml
metadata:
  subagent: release-notes
```

If that target is missing, internal-only, or malformed, activation fails
deterministically with no inline fallback. Keep routed skill metadata aligned
with real user-facing subagent definitions.

Routed skills go through the same loader + registry contract as explicit
`spawn_agent`: the next routed activation reloads the definition from disk
and inherits the parent session's `session_dir` and `project_dir` exactly
the same way. There is no separate code path for routed execution.

## Verification checklist

After creating or editing a subagent file:
1. save the file and trigger the next turn or subagent lookup
2. confirm the agent appears in `[available-subagents]`
3. run a small `spawn_agent` task to verify inherited context, tool exposure, and output
4. if missing, check daemon logs for the rejection reason

If the user has no agent files yet, `netclaw init` seeds starter definitions.
