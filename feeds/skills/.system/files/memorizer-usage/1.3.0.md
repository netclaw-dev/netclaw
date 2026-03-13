---
name: memorizer-usage
description: Memorizer MCP operations for explicit manual memory workflows under SQLite-first Netclaw memory
metadata:
  author: netclaw
  version: "1.3.0"
  triggers: organize memories | workspace management | project tracking | memory relationships | advanced search
---

## Position In The Memory Model

Netclaw's primary durable memory path is SQLite with automatic pre-turn recall.
This skill covers advanced Memorizer MCP operations for deliberate manual
organization workflows, not baseline recall.

Use this skill when you intentionally need Memorizer-specific structure:

- workspace/project organization
- memory relationships and graph linking
- metadata curation and confidence tuning
- archive/restore/version controls

## Manual-Control Expectations

- Automatic recall remains primary for normal turns.
- Explicit memory tools are intentional/manual control paths.
- Do not invoke advanced Memorizer operations reflexively on every turn.

## Core Memorizer Concepts

### Workspaces

Top-level domain containers (for example: Engineering, Homelab, Family).

- `memorizer/get_workspace`
- `memorizer/create_workspace`
- `memorizer/update_workspace`

### Projects

Goal-oriented work units with lifecycle and victory conditions.

- `memorizer/get_project_context`
- `memorizer/create_project`
- `memorizer/update_project`

### Memories

- `document`: living, editable knowledge
- `record`: historical immutable log

Useful operations:

- `memorizer/store`
- `memorizer/get`
- `memorizer/get_many`
- `memorizer/edit`
- `memorizer/update_metadata`
- `memorizer/archive_memory` / `memorizer/restore_memory`

### Relationships

Use `memorizer/create_reference` to link related knowledge (`related-to`,
`explains`, `example-of`).

## Advanced Search

`memorizer/search_memories` supports:

- `projectId`
- `filterTags`
- `minSimilarity`
- `includeUnassigned`

## Operational Notes

- If Memorizer is disconnected, do not treat that as total memory failure;
  SQLite automatic recall still exists.
- For memory subsystem health and checkpoint backlog, use `netclaw status` and
  `netclaw doctor` (`Memory Checkpoint Health`).
