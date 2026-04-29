## Why

Sessions have no concept of "which project am I working on." Project-scoped
identity files (`.netclaw/AGENTS.md`, `CLAUDE.md`) are not loaded
automatically — the `netclaw-projects` skill tells the agent to manually
`file_read` them, but that content is ephemeral: it's a tool result in one
turn's history, lost on compaction and unrecoverable after a crash. After a
restart, the agent has no idea which project it was working on and cannot
re-discover project context. This was identified as a contributor to session
amnesia alongside the cursor race and system prompt eviction fixed in PR #733.

Additionally, the agent's session directory path is not explicitly communicated
— only `media_dir` appears in the `[session]` block, forcing the agent to
infer the root by going up one level.

## What Changes

- Add a mutable, persisted `ProjectDirectory` to `WorkingContext` that tracks
  which project the session is working on. Independent of the immutable session
  directory (`~/.netclaw/sessions/{id}/`) used for state isolation.
- New `set_working_directory` tool for explicitly setting the project directory.
  Profile-managed: not exposed to Public/Team audiences by default. Validates
  target directory exists and is within audience trust profile roots.
- New `[project-instructions]` context layer loads identity files from the
  project root (`.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, `CONTEXT.md`
  — first match wins) and injects them on every LLM call via `EveryTurn`
  timing. Content is re-read from disk each turn so edits take effect
  immediately.
- `[working-context]` block includes the project directory so the agent knows
  which project it's in.
- `[session]` block includes the explicit session directory path.
- Eval: validate automatic context injection vs behavioral approach for project
  context loading.

## Capabilities

### New Capabilities

- `session-cwd`: Session-scoped project directory tracking, persistence across
  crash/restart, and the `set_working_directory` tool for setting it. Covers
  the two-directory model (immutable session directory vs mutable project
  directory) and audience gating.

- `project-instructions`: Loading of project-scoped identity files from the
  project root and injection as a `[project-instructions]` context layer with
  `EveryTurn` refresh semantics.

### Modified Capabilities

- `netclaw-tools`: `set_working_directory` tool with audience gating. Profile-
  managed so Public/Team audiences cannot use it.

- `netclaw-session`: `WorkingContext` gains `ProjectDirectory`. Persisted in
  `SessionSnapshot`. Survives compaction. `[working-context]` block includes
  project directory. `[session]` block includes session directory path. New
  `[project-instructions]` block added to context assembly.

## Impact

- **Core session state**: `WorkingContext`, `SessionSnapshot` — new field,
  serialization change.
- **Context assembly**: `SessionMessageAssembler` — new `[project-instructions]`
  block, `session_dir` added to `[session]` block.
- **New tool**: `SetWorkingDirectoryTool` — audience-gated, validates against
  trust profile roots.
- **New code**: `ProjectInstructionLayerProvider` — EveryTurn context layer.
- **Security**: Tool gated by profile-managed allowlist. Project directory
  changes validated against audience trust profile roots.
- **System skills**: `netclaw-operations` and `netclaw-projects` need updates.
- **Identity templates**: `TOOLING.md` template needs session/project directory
  guidance.
- **Eval suite**: Identity/prompt assembly changes require eval run.
- **Backward compat**: Sessions without a project directory work unchanged.
  `[project-instructions]` block not emitted when project directory is null.
