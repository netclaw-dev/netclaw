## Why

Sessions have no concept of "which project directory am I working in." Shell
commands execute in the daemon's process CWD, file tools resolve relative paths
against it, and project-scoped identity files (`.netclaw/AGENTS.md`, `CLAUDE.md`)
can only be found if the daemon happens to be in the right directory. After a
crash or restart, the agent loses project context because identity files are
loaded from disk relative to the daemon — not the project. This was identified
as a root cause of session amnesia in PR #733.

## What Changes

- Add a mutable, persisted `CurrentWorkingDirectory` to `WorkingContext` that
  tracks where the agent is "working," independent of the immutable session
  directory (`~/.netclaw/sessions/{id}/`) used for state isolation.
- Default tool CWD to the session directory at session creation. Update it when
  shell tool execution detects `cd` commands or when an explicit `set_cwd`
  command is issued.
- Shell tool (`shell_execute`) defaults `ProcessStartInfo.WorkingDirectory` to
  the session's tool CWD when no explicit working directory is provided.
- File tools (`file_read`, `file_write`, `file_edit`) resolve relative paths
  against the session's tool CWD instead of the daemon's process CWD.
- New `[project-instructions]` context layer walks up from the tool CWD to
  discover project-scoped identity files, re-read on every turn so content
  stays current across CWD changes and compaction.
- `[working-context]` block includes the current CWD.
- CWD changes are bounded by the session's audience trust profile — `cd` cannot
  escape approved roots.

## Capabilities

### New Capabilities

- `session-cwd`: Session-scoped working directory tracking, persistence across
  crash/restart, and CWD mutation via tool side effects. Covers the two-directory
  model (immutable session directory vs mutable tool CWD), initial CWD
  assignment, and CWD update semantics.

- `project-instructions`: Discovery and injection of project-scoped identity
  files (`.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, `CONTEXT.md`) by
  walking up from the session's tool CWD. Covers the `[project-instructions]`
  context layer, `EveryTurn` refresh semantics, and directory-tree walk rules.

### Modified Capabilities

- `netclaw-tools`: Shell tool defaults CWD to session tool CWD. File tools
  resolve relative paths against session tool CWD instead of daemon process CWD.
  `ToolExecutionContext` gains a `ToolWorkingDirectory` property.

- `netclaw-session`: `WorkingContext` gains `CurrentWorkingDirectory`. Persisted
  in `SessionSnapshot`. Survives compaction. `[working-context]` block includes
  CWD. New `[project-instructions]` block added to context assembly.

## Impact

- **Core session state**: `WorkingContext`, `SessionSnapshot`, `SessionState` —
  new field, serialization change.
- **Tool execution layer**: `ShellTool`, `ScopedFileAccessPolicy`,
  `ToolExecutionContext` — path resolution base changes.
- **Context assembly**: `SessionMessageAssembler` — new `[project-instructions]`
  block.
- **New code**: `ProjectInstructionWalker` — directory-tree walk utility.
- **Security**: `ScopedFileAccessPolicy` enforces CWD stays within audience
  trust profile roots. No new security surface — existing access policy gates
  still apply after path resolution.
- **System skill**: `netclaw-operations` skill needs update to document CWD
  behavior for the running agent.
- **Eval suite**: Identity/prompt assembly changes require eval run.
- **Backward compat**: Sessions without a CWD continue to work unchanged.
  `[project-instructions]` block is empty when CWD is not set.
