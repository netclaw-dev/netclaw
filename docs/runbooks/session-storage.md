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

## Managed Temporary Folder

On Linux and macOS, `temp_dir` is short, so that a Unix socket path below it
fits the socket limit (107 characters on Linux, 103 on macOS; issue #2554):

| Run | `temp_dir` |
| --- | --- |
| Parent | `/tmp/netclaw-<uid>/<key>/p` |
| Child | `/tmp/netclaw-<uid>/<key>/c<8 hex>` |

- `<uid>` is the user ID of the daemon (`id -u`). On macOS the folder is
  `/private/tmp/netclaw-<uid>`.
- `<key>` is 16 hex characters from a hash of the session envelope path. All
  runs of one session share the key folder.
- The key folder is a trusted root of its session. The folder
  `/tmp/netclaw-<uid>` is not a root. A Team or Public session cannot reach
  the key folder of another session, also when a configured root covers
  `/tmp`. A Personal session can, as it can reach each session folder.
- On Windows, `temp_dir` stays `<session-envelope>/tmp/parent` for a parent and
  `<session-envelope>/subagents/<run-id>/tmp` for a child.

The daemon runs these checks on `/tmp/netclaw-<uid>` at startup. Before each
shell launch, and before each `file_write` or `file_edit` below
`/tmp/netclaw-<uid>`, Netclaw creates `/tmp/netclaw-<uid>` and the
key folder with mode 0700. It checks that each folder is not a symbolic link,
that the daemon user owns it, and that it has no group or other permission
bits. The checks assume that `/tmp` has the sticky bit (mode 1777), as on each
supported host.

If the daemon cannot use the folder at startup, it logs an error to the daemon
log and reports a degraded `Shell/temp` connector (`shell:temp`) in
`netclaw status`, which also exits 2. The check runs again on each status
request, so a fix clears it without a restart. The daemon stays ready, so a
container supervisor does not restart it. A container with a read-only root
filesystem needs a writable `/tmp`: `docker run --read-only --tmpfs /tmp ...`.

Caution: if a check fails, the shell call or the write fails, and Netclaw does
not use another folder. The error names the folder and the cause. To recover:

1. Find who owns the folder: `ls -ld /tmp/netclaw-$(id -u)`.
2. If another user owns the folder, or if it is a link, find out why. Somebody
   can have made it to read your temp files.
3. Remove the folder, or set its mode with `chmod 700`. Netclaw creates a
   missing folder again at the next shell call. If another user owns the
   folder, only root can remove it.

The OS can delete `/tmp` files at a reboot or with `systemd-tmpfiles`. The
files in `temp_dir` are disposable. Put files that you must keep in
`artifact_dir`. Netclaw does not delete `temp_dir` when a session ends. Smoke
runs and test daemons on a developer host also leave key folders below the
same base.

A background-job record keeps the `temp_dir` that it had when it started. An
older record can therefore name `<session-envelope>/tmp/parent`.

Example: Chrome starts below `temp_dir` and makes
`/tmp/netclaw-1000/<key>/p/com.google.Chrome.XXXXXX/SingletonSocket` (77
characters).

Counterexample: Do not set `TMPDIR=/tmp/<name>` in a shell command. Files in
another `/tmp` folder are outside the session roots of a Team or Public
session, and the agent must not depend on them.

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

An authorized `file_read` can inspect each file under `config/`, for example
`config/netclaw.json` and the grant store `config/tool-approvals.json`
(owner decision D6). `netclaw.json` contains ordinary persisted
configuration, not the effective value of environment overrides.

Secret configuration belongs in protected stores. `secrets.json`, webhook route
files, key and OAuth material, database state, and process-control files remain
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
