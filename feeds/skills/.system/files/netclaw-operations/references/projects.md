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
available, declare a different user-named project before its first tool call.
Declare the named path before probing it. If rejected, declare the
user-provided fallback before other tools.
Use the task's first project path exactly; do not substitute its parent first.

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

Choose directories in this order:

1. For declared-project work, omit `WorkingDirectory`; the shell uses `project_dir`.
2. For one call in a named child directory, set typed `WorkingDirectory`.
3. Use `session_dir` for disposable writable work outside a project; do not substitute platform temporary storage.
4. Use an inline directory change only when the task requests that behavior.

Keep shell approval friction bounded:

1. Start with the smallest single shell operation that directly answers the request.
2. Use one operation per call. Add a pipeline only when the requested result requires it.
3. Do not use shell only to verify a successful structured tool result.
4. After an approval-required result, do not retry or substitute shell variants.
5. A `Tool access denied:` result is terminal; do not change scope, retry, or substitute another tool.
6. Apply one `Tool execution deferred:` correction unchanged; otherwise use a structured tool or report the block once.

The project directory is distinct from the session directory
(`~/.netclaw/sessions/{id}/`). The session directory is immutable and used for
state isolation (inbox, media). The project directory is mutable and points to
the project root.
