## Why

Source PRDs: `PRD-001`, `PRD-002`, and `PRD-006`.

Live sessions still create disposable work under shared platform temporary
roots, even after prompt guidance and interactive correction changes. Parent
agents also cannot discover child logs because session files and session logs
use separate directory trees.

## What Changes

- Give each new session one versioned storage binding with one physical
  session storage envelope.
- Store the resolved envelope root with the session. A later upgrade or
  configuration change must not reinterpret it for an existing session.
- Keep legacy session roots and log paths in place. Do not move or delete them
  during an upgrade.
- Place each child's artifacts, temporary files, and raw log below one child-run
  directory inside the parent's envelope. Return the exact child log path and
  the parent-readable child artifact directory.
- Treat the complete current session envelope as an implicit trusted root for
  parent and child runs. Existing file tools apply their ordinary audience and
  operation permissions inside it.
- Store all session directories below the trusted Netclaw sessions root.
  Parent and child runs inherit this root, so they can access other sessions
  under normal audience and operation permissions. This supports a session
  that analyzes another session's logs on a heavily used Netclaw agent.
- Set `TMPDIR`, `TMP`, and `TEMP` for each parent and child execution scope.
  The values must identify that run's managed temporary directory.
- Retire the ambiguous “session scratch” vocabulary. Use `session_dir` for the
  working and relative-path base, `temp_dir` for disposable run-local files,
  and the exact named storage path in approval rules.
- Preserve the existing `[session]` context block. Extend it with `temp_dir`,
  `artifact_dir`, `worktree_dir`, and `log_path` instead of adding another
  context mechanism.
- Extend the existing typed correction path for an explicit unmanaged
  temporary write. The correction must name the managed temporary directory
  and must not rewrite or approve the original call.
- Let agents create Git worktrees with the existing shell tool below the
  announced `worktree_dir`, then adopt the result with the existing
  `set_working_directory` tool. Do not add a worktree-specific tool,
  authorization model, or ownership record.
- Let structured file tools read `netclaw.json` when normal trusted-root and
  audience policy permits it. Keep secret stores and control-plane state
  explicitly read-denied, and keep file-write and shell policy independent.
- Replace eval assertions that treat the complete session storage envelope as
  shell cwd or disposable work. New assertions must test the `workspace/` cwd,
  managed temporary environment, and parent-child artifact discovery.
- Preserve current strict behavior when a task explicitly requires a platform
  temporary path.

**BREAKING:** New parent and child logs no longer use standalone directories in
the session-logs base. They are grouped inside the session storage envelope.
Operator runbooks that derive log paths must use the resolved paths that
Netclaw returns in session context and child results.

This change is in scope for Personal and Team sessions on POSIX and Windows.
It includes deterministic runtime tests and PII-free model evals. Automated
deletion, quotas, and a general session-storage relocation command are out of
scope.

## Representative Examples

### Managed temporary output

An agent asks a standard temporary API for a file path. Netclaw sets the
process environment before execution.

```text
TMPDIR=/srv/netclaw/sessions/s-42/tmp/parent
TEMP=/srv/netclaw/sessions/s-42/tmp/parent

standard_temp_api()
  -> /srv/netclaw/sessions/s-42/tmp/parent/result-7.tmp
```

Counterexample: Netclaw does not move the complete session below `/tmp`.
Operating-system cleanup could remove files that durable session history uses.

### Explicit user intent

The request `Run pwd from /tmp` names `/tmp` as required behavior. Netclaw
preserves that path and applies normal authorization.

Counterexample: A disposable `file_write` to `/tmp/result.log` does not express
the same requirement. An eligible Personal session receives
`UseManagedTemporaryDirectory` before an approval prompt.

### Child activity discovery

`spawn_agent` returns the exact child log path and the child's artifact
directory. The parent passes the path to `file_read`, `file_search`, or
`file_list`. Those tools apply their normal output limits and session policy.

Counterexample: The parent does not need a special child-log tool or a shell
search. Another session uses the same path access decision for that log.

Counterexample: Netclaw does not redefine `{session_dir}` as the complete
envelope. That token also participates in write and attach profiles.

### Managed worktree creation

The session context announces:

```text
worktree_dir=/srv/netclaw/sessions/s-42/worktrees
```

The agent uses `shell_execute` to run `git worktree add` with a destination
below that directory. After Git succeeds, it calls `set_working_directory`
with the created path.

Counterexample: Netclaw does not add `worktree_create` or parse Git option
grammar. A command-specific correction for a worktree below host temp is
deferred unless behavioral evidence shows that context plus environment is
not enough.

### Read ordinary configuration

An agent with normal read authority for the Netclaw configuration root calls
`file_read` for `config/netclaw.json`. The read uses the normal file-tool
contract. It does not require shell execution or a special configuration tool.

Counterexample: The same authority does not permit `file_read` of
`config/secrets.json`, key material, OAuth credentials, webhook secrets, or
the session database. It also does not permit a write to `netclaw.json`.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: Persist one collision-safe, versioned session storage
  envelope and group main and child data under that physical lineage while
  existing sessions keep their current path behavior.
- `session-cwd`: Define managed temporary and worktree areas and inject the
  standard temporary environment for every run.
- `netclaw-tools`: Expose child paths through existing file tools, compose Git
  worktree creation from existing shell and cwd tools, and permit ordinary
  configuration reads without exposing secret stores.
- `tool-approval-gates`: Return a typed managed-temp correction for eligible
  explicit writes and define the deterministic and behavioral proof boundary.

## Impact

- **Session runtime:** Session creation, recovery, child-run scope, and path
  resolution gain a versioned single-envelope storage binding.
- **Logging:** The session log dispatcher routes parent and child files into
  the session envelope. Daemon-global logs remain unchanged.
- **Tool execution:** Shell child processes receive session-specific temporary
  environment variables. File and shell tools use managed destinations.
- **Security:** The current session envelope is an implicit trusted root.
  Configured trusted roots and existing audience and operation permissions
  still decide access. Secret stores, link escapes, and control-plane state
  remain protected. This change does not claim OS-level process isolation.
- **Operations:** Runbooks and diagnostics must stop deriving session log paths.
  Cleanup must recognize both legacy and new layouts.
- **Testing:** Contract and integration tests prove layout, environment,
  recovery, access, and correction behavior. Model evals measure agent choices
  and parent-child handoff quality only.
- **Dependencies and public APIs:** No new package is required. This change
  adds no child-log tool.
