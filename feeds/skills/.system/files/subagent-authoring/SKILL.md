---
name: subagent-authoring
description: "How to create and troubleshoot file-defined subagents in ~/.netclaw/agents. Load when the user asks to add, edit, or debug subagent definitions, or when a skill routes via metadata.subagent."
metadata:
  author: netclaw
  version: "1.6.2"
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

## Working context

A spawned subagent receives the parent turn's project directory and recent-file
snapshot in its initial runtime context. The child tracks its own first-party
file reads and edits for the lifetime of the run; it never mutates the parent's
durable working context directly. On successful completion Netclaw returns a
structured file handoff and merges confirmed child edits into the parent for
the next turn. Git changes merely observed in a shared worktree are reported
without claiming that the child authored them.

## When to use

Load this when the user asks to:
- create a new subagent
- edit advisory tool metadata, prefill timeout, or behavior for an existing subagent
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
| `prefillTimeoutSeconds` | inherits `SubAgents.PrefillTimeoutSeconds` (`1800`) | Generous wait-for-first-token budget covering queue wait and cold prefill. Content-free `prompt_progress` keepalives refresh it, so a slow self-hosted prefill is not mistaken for a hang; the inter-delta budget takes over once the first real token arrives. Raise this for heavyweight agents on slow/large-context models. |
| `visibility` | `user-facing` | Accepts `user-facing`, `UserFacing`, `internal`, or `Internal`. Invalid values make the file fail loud and skip loading. |
| `emitStructuredFindings` | `false` | When true, successful output is emitted as findings for parent-session review. |

Unknown fields are ignored. A file that still sets `timeoutSeconds` loads. The
loader logs one warning for that file: the key is obsolete and ignored. Delete the
key from the file.

The inter-delta budget is not a per-agent setting. Every subagent uses
`Session.FirstTokenTimeoutSeconds` (default `600`) from `netclaw.json`. A
profile that used the old `timeoutSeconds` default of `60` now waits up to `600`
seconds between deltas.

Every sub-agent LLM call also has a config-level **no-progress ceiling**
(`SubAgents.NoProgressTimeoutSeconds` in `netclaw.json`, default `1200` = 20 min).
Text, tool calls, and thinking refresh it. Content-free keepalives never refresh
it. When it elapses, the run fails with reason `no_substantive_output_timeout`.
It is not a per-agent frontmatter field. Tune it in `netclaw.json`.

Two more rules end a run with outcome `failed`:

- `thinking_only_timeout`: one model call streamed only thinking, with no text
  and no tool call, for three times the no-progress ceiling (default 60 min).
  Thinking refreshes the ceiling, so this cap stops a model that thinks without
  end. A text delta with visible characters or a tool-call delta restarts the
  cap. A reasoning part never restarts it, also when its text is empty. The cap
  counts from the start of the call, so prefill time counts.
- `output_truncated`: the model reply stopped at its output limit and has no
  tool call. The text is cut off, so it is not a result. The run does not retry.
  The failure text holds a short, redacted preview of the cut-off text. The
  runtime also saves the full redacted answer text as `truncated-output.md` in
  the run artifact folder, marked as partial, and the failure text names its
  path. A thinking-only reply saves no file. A reply that has a tool call still
  runs its tools.

When a subagent fails with `output_truncated`, ask for a shorter answer. If the
subagent has a file write tool, tell it to write long output to a file and to
return the file path.

When a subagent fails with `thinking_only_timeout`, ask for a simpler task, or
use a model that thinks less. A larger output limit does not help. To allow
longer thinking, raise `SubAgents.NoProgressTimeoutSeconds` in `netclaw.json`.
The cap is three times that value.

## Example: valid subagent definition

```markdown
---
name: notion-planner
description: Summarizes local daily planning notes for the parent session
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

Parent sessions and subagents detect repeated calls from exact arguments and actual results.
After two equal feedback rounds, the runtime refuses the next matching call and returns a correction.
A repeat after that correction ends the turn. Use a different approach after a correction.
Useful work can exceed the former iteration limits. Deadlines, cancellation, and tool policy still apply.

`spawn_agent` and routed skills return a durable accepted run ID before child completion.
Continue independent parent work. When the child ends, a notice arrives as a user message.
The notice names the run, the outcome, and the path of `result.md`. Read that file with `file_read`.
The result never arrives as tool output.
Acceptance does not prove success. `result.md` carries outcome, evidence, and authorized artifact paths.
Use `check_agent_run` for necessary status or cancellation. Do not poll in a loop.
The original owner retains authority. An unrelated child approval does not block parent progress.
A cancellation acknowledgement does not mean completion. Wait for the notice and read `result.md`.
Recovery reports uncertain child loss without an automatic repeat of external work.
A failed evidence review cannot adopt the raw child context.

Use `agent-coordination` for task composition, worktree ownership, and independent review.
Load it by canonical name. Read its optional workflow resources through `skill_read_resource`.

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
