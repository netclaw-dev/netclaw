# Memorizer Usage

<!-- description: How to use the Memorizer MCP server for cross-session knowledge management -->

## Overview

Memorizer is an MCP-based knowledge store for durable, cross-session memory.
Use it to save project context, solutions, research findings, and any factual
knowledge that should persist beyond the current conversation.

Check availability: if `search_memories` returns results or a connection error,
Memorizer is configured. If it returns "not available," it is not connected.

## Core Concepts

### Workspaces

Workspaces are top-level organizational containers representing major domains
(e.g., "Engineering", "Homelab", "Family"). They persist indefinitely and
can be nested.

- Use `memorizer/get_workspace` (no args) to list existing workspaces
- Use `memorizer/create_workspace` to create new domains
- Don't create workspaces for every topic — they're for **broad, persistent
  domains** that will accumulate many memories over time

### Projects

Projects are goal-oriented, completable units of work within a workspace
(e.g., "Migrate to K8s", "Fix auth bug", "Q1 marketing campaign").

- Projects have a lifecycle: `draft` → `active` → `completed`/`archived`
- Projects can have **victory conditions** — what success looks like
- Projects can be nested (subprojects under a parent)
- Use `memorizer/get_project_context` to list projects in a workspace
- Use `memorizer/create_project` to start a new goal-oriented initiative
- Use `memorizer/update_project` to change status when work completes

### Memories (Documents and Records)

Two archetypes:

| Archetype | Mutability | Use For |
|-----------|-----------|---------|
| `document` | Living, editable | Solutions, guides, project notes, evolving knowledge |
| `record` | Historical, immutable | Work logs, decisions, meeting notes, audit trails |

- Use `memorizer/store` to create a new memory
- Use `memorizer/edit` to update a document's content (find-and-replace)
- Use `memorizer/update_metadata` to change title, tags, or confidence
- Use `memorizer/get` to retrieve a specific memory by ID
- Use `memorizer/get_many` to fetch several related memories at once

### Relationships

Memories can be linked with typed directional references:

| Type | Meaning |
|------|---------|
| `related-to` | General association between memories |
| `explains` | One memory explains or elaborates on another |
| `example-of` | One memory is a concrete example of another |

Use `memorizer/create_reference` to link related knowledge. Relationships
help with future retrieval — when you find one memory, related ones surface.

### Search

`search_memories` (the built-in meta-tool) wraps Memorizer's search. It
performs vector similarity search with optional filtering:

- **Tags**: Filter by tags like `reference`, `how-to`, `coding-standard`
- **Project scope**: Pass `projectId` to search within a specific project
- **Minimum similarity**: Adjust threshold (default 0.7) for broader/narrower results

For direct Memorizer access (advanced), use `memorizer/search_memories` which
supports additional parameters like `includeUnassigned`.

## Organization Patterns

### The Unfiled Inbox

Memories without a workspace or project go to "Unfiled" — think of it as
an inbox. Periodically organize Unfiled memories into the right workspace or
project using `memorizer/move_memory`.

**Good practice:** When saving a memory, assign it to a project if one exists
for the current work. Otherwise, save to a workspace. Only leave things Unfiled
if you're unsure where they belong.

### Tagging Strategy

Use consistent tags to aid retrieval:

- `reference` — factual knowledge, documentation
- `how-to` — procedures, step-by-step guides
- `decision` — architectural or design decisions
- `troubleshooting` — problems encountered and solutions
- `coding-standard` — conventions and patterns
- `todo` — items to follow up on later

### Confidence Scores

Set confidence (0.0–1.0) to indicate reliability:

- **1.0** — Verified fact, confirmed by user
- **0.7–0.9** — High confidence from reliable source
- **0.5–0.6** — Reasonable inference, may need verification
- **Below 0.5** — Speculative, flag for review

## When to Save

Save to Memorizer **proactively** during conversation when you encounter:

- Solutions to problems (especially non-obvious ones)
- User-confirmed facts about their projects or environment
- Architectural decisions and their rationale
- Research findings that may be relevant later
- Patterns or conventions discovered in codebases

**Don't save:**

- Session-specific ephemeral context (task in progress, temporary state)
- Information already in identity files (SOUL.md, AGENTS.md, TOOLING.md)
- Unverified assumptions or speculative conclusions
- Duplicate information — search first, then update existing or create new

## Archiving and Versioning

- Use `memorizer/archive_memory` to mark obsolete content (hidden from search
  but preserved for history)
- Use `memorizer/restore_memory` to bring archived content back
- Use `memorizer/get` with `includeVersionHistory=true` to see edit history
- Use `memorizer/revert_to_version` to undo unwanted changes

## Memory Quality

Write memories that are **self-contained and richly detailed**. A future agent
(or your future self) should be able to understand them without the original
conversation.

**Good memory:**
- Title: "Kubernetes pod restart loop fix — OOM on metrics sidecar"
- Type: troubleshooting
- Tags: [troubleshooting, kubernetes, homelab]
- Content: Full markdown with the problem description, investigation steps,
  root cause, solution (with config snippets), and verification commands.

**Bad memory:**
- Title: "K8s fix"
- Content: "Fixed the pod restart issue by increasing memory limits."

Include: code blocks, configuration snippets, command output, links, tables,
step-by-step instructions. The more context, the more useful on retrieval.

## Example Workflow

1. User describes a deployment problem with their K8s cluster
2. Search existing memories: `search_memories` with "kubernetes deployment"
3. If no prior context, investigate and solve the problem
4. Save the solution: `memorizer/store` with type="how-to",
   tags=["troubleshooting", "kubernetes"], assigned to the relevant project
5. Link to related memories if any: `memorizer/create_reference`
6. Next session: `search_memories` retrieves the solution automatically
