# Session Storage Runbook

Use this runbook to inspect session files after an upgrade.
See the [engineering glossary](../spec/GLOSSARY.md#filesystem-and-output-terms) for the shared terms.

## Storage Layouts

Netclaw gives each new session one durable version-2 storage binding.
The binding stores the absolute [session storage envelope](../spec/GLOSSARY.md#session-storage-envelope).
The session workspace is the envelope's `workspace/` directory.
The complete envelope is an implicit trusted root. It is not the workspace,
default shell cwd, or an unconditional shell grant.

Existing sessions have no binding.
They keep their established workspace and log paths.
Netclaw does not move, copy, or rename their files.
The current binary resolves those paths with the current legacy rules.
If an operator changes a legacy storage root, Netclaw does not move or
rediscover the files that remain below the old root.

Example: A configuration change does not change an existing version-2 binding.

Counterexample: Netclaw does not recompute only the log path from the new configuration.

## Parent And Child Paths

The `[session]` context gives a private run these exact paths:

- `session_dir` is the workspace and default relative-path base.
- `temp_dir` contains disposable files for the current run.
- `artifact_dir` contains outputs that the parent or user must keep.
- `worktree_dir` contains Git worktrees for the session.
- `log_path` is the raw log for the current run.

Netclaw sets `TMPDIR`, `TMP`, and `TEMP` for each child process.
Standard native and .NET temporary APIs therefore select `temp_dir`.
Netclaw does not change the daemon process environment.

Example: A child writes a diagnostic archive below its own `temp_dir`.

Counterexample: A child does not treat `session_dir` as disposable storage.

## Log Access

The current session envelope is an implicit trusted root for parent and child
runs. Existing file and shell tools apply their normal audience, operation,
syntax, and approval rules inside it. Runs also inherit configured trusted
roots. Another session is accessible only when those ordinary roots and
permissions already cover it.

A successful `spawn_agent` result supplies the exact child log path.
Use that path with an existing file tool.
Do not search the global log directory with a shell command.

Example: `file_read` reads the returned child `LogPath` while the child writer remains open.

Counterexample: A read-only audience cannot use `file_write` on the same log.
Path containment does not replace operation permission.

## Configuration Reads

An authorized `file_read` can inspect the exact `config/netclaw.json` path.
That file contains ordinary persisted configuration, not the effective value
of environment overrides.

Secret configuration belongs in protected stores. `secrets.json`, key and OAuth
material, webhook secrets, database state, and process-control files remain
read-denied. Reading `netclaw.json` does not grant write, attach, or shell
authority.

## Managed Worktrees

Use the `worktree_dir` from the existing `[session]` context for a new Git
worktree. Run `git worktree add` through `shell_execute` with a destination
below that directory. After Git succeeds, pass the created path to
`set_working_directory`.

Normal shell authorization decides the Git command. Netclaw does not expose a
special worktree tool or delete the worktree when the session ends.

Example: Git creates `<worktree_dir>/fix-session-temp`, then
`set_working_directory` adopts that path.

Counterexample: A failed Git call does not change project scope.

## Upgrade Checks

Use these checks after an upgrade:

1. Resume one existing session and confirm its workspace and log paths stay unchanged.
2. Start one new session and record its `session_dir` and `log_path`.
3. Restart the daemon and confirm the new paths stay unchanged.
4. Start one child and read its returned log path with `file_read`.
5. Confirm another session follows its ordinary trusted-root and audience
   policy for that log.

Warning: A pre-feature binary cannot resume a session that has a version-2 binding.
This downgrade path is outside the supported scope.
