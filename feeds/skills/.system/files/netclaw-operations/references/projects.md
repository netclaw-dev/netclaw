# Project Directory


## Project Directory


Sessions track a **project directory** — the root of the codebase or project
you are currently working on. When set, the project's identity file (checked in
order: `.netclaw/AGENTS.md`, `CLAUDE.md`, `AGENTS.md`, `CONTEXT.md` — first
match wins) is automatically loaded into the system prompt alongside the global
SOUL/AGENTS/TOOLING layers.

Use `set_working_directory` to set or change the project directory:

```
set_working_directory(path: "/workspace/service")
```

An absolute shell path gives approval policy an exact candidate scope. It does
not add that directory as a safe-space root. When the declaration tool is
available, declare a different user-named project before several shell calls.

Rules:

- The path must be an absolute path to an existing directory
- The path must be within the session's allowed file access roots
- Profile-managed: granted to Team and Personal audiences by default, not Public
- Project identity files are re-read from disk on each `SetSystemPrompt()` call,
  so edits to the project's `AGENTS.md` take effect on the next project switch
  or daemon restart
- The project directory persists across crash/restart via `WorkingContext`
- The `[working-context]` block includes `project_dir:` so you always know which
  project is active
- Do not call the tool again when `project_dir` already names the right project
- A failed call does not change the project directory. Correct the path and
  retry the tool before you continue.
- For one shell call in another directory, use the `shell_execute`
  `WorkingDirectory` argument. Do not add an inline `cd` unless changing
  directory is itself the behavior the user asked you to run or test.

The project directory is distinct from the session directory
(`~/.netclaw/sessions/{id}/`). The session directory is immutable and used for
state isolation (inbox, media). The project directory is mutable and points to
the project root.
