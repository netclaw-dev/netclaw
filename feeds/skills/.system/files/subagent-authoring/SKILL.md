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
name: research-assistant
description: Deep web research with search and citation
tools: [web_search, web_fetch, file_read, attach_file]
---

You are a research assistant.
```

## Required frontmatter fields

These are required for a file to load:
- `name` (string, non-empty)
- `description` (string, non-empty)
- `tools` (non-empty list)

The markdown body below the closing `---` must also be non-empty.

## Optional frontmatter fields and defaults

| Field | Default | Notes |
|------|---------|-------|
| `modelRole` | `Compaction` | `Main` or `Compaction` (case-insensitive). Invalid values fall back to `Compaction`. |
| `timeoutSeconds` | `60` | Wall-clock timeout for subagent execution. |
| `visibility` | `user-facing` | Accepts `user-facing`, `UserFacing`, `internal`, or `Internal`. Invalid values fall back to `user-facing`. |
| `emitStructuredFindings` | `false` | When true, successful output is emitted as findings for parent-session review. |

Unknown fields are ignored.

## Tool policy for file-defined agents

File-defined subagents are validated against a conservative allowlist:
- `attach_file`
- `file_read`
- `web_fetch`
- `web_search`

If any other tool appears in `tools`, the file is rejected.

Important: this allowlist applies to file-defined agents in `~/.netclaw/agents`.
Platform-owned internal subagents with broader tools are code-registered, not
loaded from this directory.

## Example: valid subagent definition

```markdown
---
name: release-notes
description: Draft concise release notes from local changes
tools: [file_read]
modelRole: Compaction
timeoutSeconds: 90
visibility: user-facing
emitStructuredFindings: false
---

You are a release-notes assistant.

## Goal

Summarize user-visible changes from local project files.

## Rules

- Use file_read to inspect release notes, changelogs, and docs.
- Keep output concise and structured with markdown headings.
- Include file paths for each notable change.
- Do not invent changes that are not in source files.
```

## Fail-loud loader behavior

At startup, invalid files are skipped with warnings. Common rejection reasons:
- missing or unparseable YAML frontmatter
- missing required fields (`name`, `description`, `tools`)
- empty markdown body
- empty `tools` list
- disallowed tool names
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
