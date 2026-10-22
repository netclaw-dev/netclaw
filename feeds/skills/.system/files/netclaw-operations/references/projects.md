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
3. Use `temp_dir` for disposable files. Standard temporary APIs already use this directory.
4. Use an inline directory change only when the task requests that behavior.

Keep shell approval friction bounded:

1. Start with the smallest single shell operation that directly answers the request.
2. Use one operation per call. Keep independent searches and diagnostics separate; do not join them with separators or labels.
3. Add a pipeline only when the requested result requires it.
4. Do not use shell only to verify a successful structured tool result.
5. If approval is required but no interactive requester is available, do not retry or substitute the call during that turn.
6. After a person denies a call, do not retry it, and do not reach its goal another way during the same user turn. Tell the user what you wanted to do, and ask how to continue.
7. Do not change its scope or substitute another tool to evade the denial.
8. A later explicit user request can start a new call. Apply the normal approval policy to that call.
9. Apply all compatible advice in a correction response before the next call.
10. A shell call can return correction advice under Auto. Auto removes approval prompts; it does not remove corrections.
11. Advice grants no authority. Every replacement call passes current policy.
12. If you require the exact platform path, retry unchanged once through normal policy.
13. Reviewed diagnostics without file output do not receive temporary relocation advice. Normal approval and denial rules still apply.

The project directory is distinct from the session directory
(`~/.netclaw/sessions/{id}/`). The session directory is immutable and used for
state isolation (inbox, media). The project directory is mutable and points to
the project root.

For a Git worktree, choose a destination below `worktree_dir`. Use
`shell_execute` to run Git. Use `set_working_directory` only after Git succeeds.

## Temporary Folder

On Linux and macOS, `temp_dir` is a short folder: `/tmp/netclaw-<uid>/<key>/p` for a
parent run, and `/tmp/netclaw-<uid>/<key>/c<id>` for a child run. On macOS the folder
is `/private/tmp/netclaw-<uid>/...`. A Unix socket path below it fits the socket limit
(107 characters on Linux, 103 on macOS). Chrome, Playwright, and `ssh` control sockets
work there. On Windows, `temp_dir` stays inside the session folder.

- Netclaw sets `TMPDIR`, `TMP`, and `TEMP` for each shell process. Do not set them in a
  shell command. Do not make your own folder in `/tmp`.
- The file tools can read and write `temp_dir`. A Team or Public session cannot reach the
  `temp_dir` of another session. A Personal session can, as it can reach each session folder.
- If a program fails with `Socket path too long`, report a Netclaw defect. Do not work
  around it with `TMPDIR=/tmp/...`.
- If a shell call fails with `The managed temporary folder ... is a symbolic link`,
  `is owned by`, or `allows group or other access`, tell the operator. Do not retry with
  another temporary folder. The operator fixes or removes the named folder. Netclaw does
  not change a folder that exists.
- The OS can delete files in `/tmp`. Netclaw does not clean `temp_dir`.

## File Reach of the Personal Audience

The shell trust zone follows the `WriteFiles` profile. For the Personal audience,
`file_read`, `file_list`, `file_search`, and `attach_file` reach each path in `WriteFiles`.

- `WriteFiles` mode `All` lets these tools reach each path that is not protected.
- `WriteFiles` mode `Roots` adds its roots to the read and attach roots.
- Protected paths stay denied. `set_working_directory` stays inside the read roots.
- Team and Public do not change.
- `netclaw doctor` warns when a profile can write wider than it can read. The warning
  has no auto-fix. Tell the operator to narrow `WriteFiles` or widen `ReadFiles`.
