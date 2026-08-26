## Why

Source PRDs: `PRD-001`, `PRD-002`, and `PRD-006`.

Live sessions still create disposable work under shared platform temporary
roots, even after prompt guidance and interactive correction changes. Parent
agents also cannot discover child logs because session files and session logs
use separate directory trees.

## What Changes

- Give each new session one versioned storage descriptor with an agent-data
  root and a protected audit root.
- Store both resolved roots with the session. A later upgrade or configuration
  change must not reinterpret either root for an existing session.
- Keep legacy session roots and log paths in place. Do not move or delete them
  during an upgrade.
- Place each child's artifacts below the parent agent-data root and its raw log
  below the parent audit root. Return opaque log and artifact references to the
  parent.
- Keep raw audit logs outside normal workspace-file access. Provide a bounded,
  redacted parent view of child activity instead of requiring shell searches.
- Set `TMPDIR`, `TMP`, and `TEMP` for each parent and child execution scope.
  The values must identify that run's managed temporary directory.
- Extend the existing typed correction path for an explicit unmanaged
  temporary write. The correction must name the managed temporary directory
  and must not rewrite or approve the original call.
- Add a deferred structured worktree tool. The tool chooses a session-owned
  worktree path, creates the worktree, returns its working directory, and
  records cleanup ownership.
- Replace eval assertions that require the session root as the shell working
  directory for all disposable work. New assertions must test the managed
  temporary environment and parent-child artifact discovery.
- Preserve current strict behavior when a task explicitly requires a platform
  temporary path.

**BREAKING:** New child logs no longer use standalone sibling directories in
the session-logs base. They are grouped below the parent audit root. Operator
runbooks that derive child log paths must use the supported inspection command.

This change is in scope for Personal and Team sessions on POSIX and Windows.
It includes deterministic runtime tests and PII-free model evals. Automated
deletion, quotas, and a general session-storage relocation command are out of
scope.

## Representative Examples

### Managed temporary output

An agent asks a standard temporary API for a file path. Netclaw sets the
process environment before execution.

```text
TMPDIR=/home/agent/.netclaw/sessions/s-42/tmp/parent
TEMP=/home/agent/.netclaw/sessions/s-42/tmp/parent

standard_temp_api()
  -> /home/agent/.netclaw/sessions/s-42/tmp/parent/result-7.tmp
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

`spawn_agent` returns an opaque child log reference. The parent passes that
reference to `subagent_log_read` and receives a bounded activity page.

Counterexample: The parent does not receive a raw log path. It does not search
the daemon log tree with shell commands.

### Managed worktree creation

`worktree_create` accepts an authorized source repository and a branch. The
tool allocates the destination below the session worktree area.

Counterexample: The tool does not accept `/tmp/fix-branch` as a destination.
Netclaw does not parse private Git options to authorize that shell command.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-session`: Persist the versioned agent-data and audit roots and group
  main and child session logs under one protected lineage with legacy resume
  behavior.
- `session-cwd`: Define managed temporary and worktree areas and inject the
  standard temporary environment for every run.
- `netclaw-tools`: Expose bounded child-log discovery and a managed deferred
  worktree operation without exposing raw audit files.
- `tool-approval-gates`: Return a typed managed-temp correction for eligible
  explicit writes and define the deterministic and behavioral proof boundary.

## Impact

- **Session runtime:** Session creation, recovery, child-run scope, and path
  resolution gain a versioned two-root storage descriptor.
- **Logging:** The session log dispatcher routes parent and child files into
  one protected session audit hierarchy. Daemon-global logs remain unchanged.
- **Tool execution:** Shell child processes receive session-specific temporary
  environment variables. File and worktree tools use managed destinations.
- **Security:** Path knowledge grants no authority. Raw audit logs remain
  protected, and all replacement calls pass normal authorization.
- **Operations:** Runbooks and diagnostics must stop deriving session log paths.
  Cleanup must recognize both legacy and new layouts.
- **Testing:** Contract and integration tests prove layout, environment,
  recovery, access, and correction behavior. Model evals measure agent choices
  and parent-child handoff quality only.
- **Dependencies and public APIs:** No new package is required. Any new tool
  schema remains internal to Netclaw's existing tool registry.
