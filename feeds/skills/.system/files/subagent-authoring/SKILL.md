---
name: subagent-authoring
description: "How to create and troubleshoot file-defined subagents in ~/.netclaw/agents. Load when the user asks to add, edit, or debug subagent definitions, or when a skill routes via metadata.subagent."
metadata:
  author: netclaw
  version: "1.0.0"
---

# Subagent Authoring

Use this skill when you need to create, update, or debug subagent definitions.

## When to use

Load this when the user asks to:
- create a new subagent
- edit tools, timeout, or behavior for an existing subagent
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

With tools restricted to a specific set:

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
| `tools` | (inherit all) | List of tool names. When omitted, the subagent inherits all session tools including MCP tools. When specified, acts as a whitelist to limit access. |
| `modelRole` | `Compaction` | `Main` or `Compaction` (case-insensitive). Invalid values fall back to `Compaction`. |
| `timeoutSeconds` | `60` | Wall-clock timeout for subagent execution. |
| `visibility` | `user-facing` | Accepts `user-facing`, `UserFacing`, `internal`, or `Internal`. Invalid values fall back to `user-facing`. |
| `emitStructuredFindings` | `false` | When true, successful output is emitted as findings for parent-session review. |

Unknown fields are ignored.

## Example: valid subagent definition

```markdown
---
name: notion-planner
description: Automates daily planning workflow in Notion
timeoutSeconds: 120
---

You are a planning assistant that works with Notion.

## Goal

Create and update daily plans in the user's Notion workspace.

## Guidelines

- Use Notion MCP tools to search, fetch, and create/update pages
- If you encounter connectivity issues, report them clearly
- Follow the user's existing plan format and structure
```

This agent inherits all session tools (including Notion MCP tools) because no
`tools` field is specified.

## Fail-loud loader behavior

At startup, invalid files are skipped with warnings. Common rejection reasons:
- missing or unparseable YAML frontmatter
- missing required fields (`name`, `description`)
- empty markdown body
- duplicate `name` across files (first file in stable sorted order wins)

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

## Verification checklist

After creating or editing a subagent file:
1. restart `netclawd` (subagent files are loaded at startup)
2. confirm the agent appears in `[available-subagents]`
3. run a small `spawn_agent` task to verify tools and output
4. if missing, check daemon logs for the rejection reason

If the user has no agent files yet, `netclaw init` seeds starter definitions.
