## Context

See [proposal.md](proposal.md) for the reason for this change. This design uses
the terms in the [engineering glossary](../../../docs/spec/GLOSSARY.md).

Netclaw now computes agent-visible session files below
`NetclawPaths.SessionsDirectory`. It computes session audit logs below the
separate `NetclawPaths.SessionLogsDirectory` tree. The split deliberately keeps
raw audit data outside workspace-file authority.

The current platform-temp correction sends an eligible Personal agent from the
host temporary root to the session directory. Parent and child prompts also
describe the complete session directory as scratch. Live evidence shows that
the prompt does not control every model choice.

The current eval suite asserts that a child passes the complete session
directory as `WorkingDirectory`. The suite finds child logs by a global path
search under `logs/sessions`. This proves model alignment to the old layout. It
does not prove a deterministic temporary environment or parent log discovery.

`SessionDirectoryHelper.IsUnderTempPath` warns when the complete sessions tree
is below the operating system temporary root. Attachments and journal
references can outlive that filesystem data. This constraint prevents the
default root from moving to `/tmp` or `%TEMP%` in this change.

## Goals / Non-Goals

**Goals:**

- Give one session one durable storage descriptor and one child-run lineage.
- Keep raw logs protected while the parent can inspect bounded child activity.
- Make standard temporary APIs resolve to a session-owned directory.
- Preserve an existing session's resolved location across an upgrade.
- Give worktrees a managed location and an explicit owner.
- Keep all authority decisions outside model prose.
- Separate deterministic contract proof from model-alignment evidence.

**Non-Goals:**

- Move the default sessions base below the operating system temporary root.
- Delete a session, artifact, output, temporary file, or worktree.
- Add a quota or retention policy.
- Move an existing session to another configured Netclaw home.
- Let an agent read its own raw audit log.
- Infer private command-line grammar in shell policy.
- Add speculative Windows path patterns without observed evidence.

## Decisions

### Ownership and data lifetime

Each state item has one owner and one lifetime.

| State | Owner | Lifetime | Consumer |
|---|---|---|---|
| Storage descriptor | Parent session actor | Durable | Session paths, log dispatcher, child run scope |
| Managed temporary path | Parent or child run scope | Run lifetime; files persist until later cleanup | Process environment and working context |
| Captured host temporary root | Shell approval policy | Daemon process lifetime | Generic path comparison |
| Managed-temp correction key | Parent or child actor | One user turn | Approval bridge |
| Child log reference | Parent session actor | Durable session lifetime | `subagent_log_read` |
| Child audit records | Log dispatcher | Durable file lifetime | Redacted projection builder |
| Worktree ownership record | Parent session actor | Durable until later cleanup | Future cleanup policy |

The model does not own any authority state. Model text can request a new call,
but it cannot change a root, grant, or correction key.

### Ordered runtime flow

The following flow is schematic. It omits actor delivery retries and normal
tool authorization stages.

```text
1. Parent session actor loads or persists SessionStorageDescriptor.
2. Parent session actor creates an immutable run scope.
3. Run scope derives session_dir, temp_dir, artifact_dir, and AuditRoot.
4. Process launcher creates temp_dir and injects TMPDIR, TMP, and TEMP.
5. Tool policy evaluates each authored tool call.
6. Eligible unmanaged-temp writes return UseManagedTemporaryDirectory.
7. The actor commits that correction and arms one turn-local retry key.
8. A replacement call passes complete authorization as a new call.
9. A child run writes its raw log below the parent AuditRoot.
10. The parent reads bounded child activity through the opaque reference.
```

Counterexample: A prompt cannot replace steps 1 through 8. A compliant model
does not prove that the runtime injected or enforced these values.

### Bind separate agent-data and audit roots

Layout version 2 will keep the current durable sessions base for agent-visible
data and the current session-logs base for protected audit data. One persisted
descriptor will bind both roots to the same session.

```text
agent-data root
<sessions-base>/<session-id>/
├── artifacts/
├── inbox/
├── media/
├── tool-calls/
├── tmp/
│   ├── parent/
│   └── subagents/<run-id>/
├── worktrees/
└── subagents/
    └── <run-id>/artifacts/

protected audit root
<session-logs-base>/<session-id>/
├── session.log
└── subagents/
    └── <run-id>/session.log
```

The audit root is not added to workspace-file or shell safe spaces by normal
session context. The parent can inspect a bounded child-activity projection
through a dedicated tool. Session ownership alone cannot read the raw files.

Netclaw rejected placing raw logs below the agent-data root. A shell starts in
that root when no project exists. A recursive command could then read a raw log
without exposing its private traversal semantics to Netclaw policy. A protected
subdirectory would therefore depend on executable-specific parsing or
operating-system isolation that this change does not provide.

An alternative placed the complete tree below `/tmp/netclaw` or `%TEMP%`.
Netclaw rejected this default because operating system cleanup can remove
attachments that durable turn history still references. An operator can move
the complete Netclaw home through the existing configuration boundary, but
that choice remains subject to the current doctor warning.

**Example:** A parent starts child `run-7`. The child writes artifacts below
`subagents/run-7/artifacts` and logs below `AuditRoot/subagents/run-7`.

**Counterexample:** Netclaw does not place `session.log` below `session_dir`.
A recursive shell command from `session_dir` must not reach the raw audit log.

### Bind one durable storage descriptor to each session

The main session actor will own one immutable storage descriptor:

```text
SessionStorageDescriptor
  LayoutVersion
  SessionRoot       // agent-visible data
  AuditRoot         // raw parent and child logs
  LegacyLogRoot?   // version 1 only
```

The actor will persist the descriptor before the first version-2 filesystem
side effect. A child will receive the descriptor in its immutable run scope.
The child will derive all paths from the parent roots and its opaque run ID.

The following pseudocode is schematic. It omits persistence retries and actor
message details.

```text
resolve_storage(session_state, configured_paths):
    if session_state has a storage descriptor:
        return that descriptor

    if the session predates layout version 2:
        descriptor = legacy paths under the current Netclaw home
    else:
        descriptor = version 2 agent and audit roots

    persist descriptor before a filesystem consumer receives it
    return descriptor
```

Both absolute roots are durable data. A later environment override or binary
upgrade cannot recalculate them. A rollback can still resume the session
because version 2 keeps the existing session base. An old binary can write a
new legacy log file after rollback. The next new binary must inspect both
recorded log lineages for that session.

An alternative derived every path from the current `NETCLAW_HOME`. That design
would repeat the configuration conflict that caused prior workspace failures.

**Example:** A session binds roots under `/srv/netclaw-a`. An operator later
changes the configured home to `/srv/netclaw-b`. The session still uses the
two persisted roots under `/srv/netclaw-a`.

**Counterexample:** Netclaw does not recompute one root while it retains the
other root. That split would send one session to two unrelated lineages.

### Route session logs by storage descriptor and child run ID

The log dispatcher will receive a resolved log target. It will not derive a
path from a session ID alone.

```text
main diagnostic
  -> session descriptor
  -> <audit-root>/session.log

child diagnostic
  -> session descriptor + run id
  -> <audit-root>/subagents/<run-id>/session.log
```

The dispatcher still owns one serialized writer per target. Daemon-global logs
remain under the existing daemon log path.

The parent does not receive raw filesystem access to the audit root. The
`spawn_agent` result will return the child run ID and an opaque log reference.
A deferred `subagent_log_read` tool will return a bounded, redacted activity
projection. It will support a cursor and an optional literal query. It will not
return system prompts, credentials, approval payloads, or unredacted tool data.

This tool avoids two bad alternatives. Raw file access would expose the audit
trail. A shell search would require the model to know daemon internals and
would create approval friction.

```text
spawn_agent result
  run_id: "run-7"
  activity_log_ref: "child-log:opaque-value"

subagent_log_read(activity_log_ref, cursor: null)
  -> bounded redacted records + next cursor
```

**Counterexample:** The result does not contain
`/var/lib/netclaw/logs/sessions/s-42/subagents/run-7/session.log`. The parent
does not use `find`, `grep`, or `cat` to inspect that file.

### Give each run one managed temporary environment

The parent path is `<session-root>/tmp/parent`. A child path is
`<session-root>/tmp/subagents/<run-id>`. Netclaw will create and validate the
directory before it starts a shell or another child process.

Each process receives these process-local values:

```text
TMPDIR = <managed-temp>
TMP    = <managed-temp>
TEMP   = <managed-temp>
```

Netclaw will set all three values on POSIX and Windows. The host process
environment remains unchanged. On Windows, `TMP` and `TEMP` drive native and
.NET temporary-path selection. `TMPDIR` supports cross-platform programs.

The model context will name `session_dir`, `temp_dir`, and `artifact_dir` once.
It will use one short rule for each path. The environment remains the primary
mechanism. Prompt text is not the security or correctness boundary.

**Example:** A .NET process calls `Path.GetTempPath()`. The result is the
current run's `temp_dir` because Netclaw injected the environment first.

**Counterexample:** The model does not need to author
`TMPDIR=<temp_dir> command`. Netclaw also does not change the daemon's global
environment for sibling runs.

### Keep the existing session cwd fallback

A shell call with no project and no explicit `WorkingDirectory` will still use
the session root. This preserves the existing no-project session behavior.

Programs that request a temporary path will use `temp_dir` through the injected
environment. The agent does not need to author `cd`, export variables, or add
an environment prefix to each command.

This distinction prevents a no-project conversation from starting in an
internal directory. It also prevents temporary SDK output from mixing with
artifacts and inbound files.

**Example:** A no-project shell starts in `session_dir`. A library inside that
shell creates its cache below `temp_dir` through the standard environment.

**Counterexample:** Netclaw does not use `temp_dir` as the shell cwd. A final
attachment also does not belong in `temp_dir`; it belongs in `artifact_dir`.

### Extend the existing correction with one managed-temp code

`UseManagedTemporaryDirectory` will be a closed remediation code. The
correction presenter will name the exact managed path from trusted invocation
context.

The correction applies only when Netclaw has a generic fact that proves the
agent authored an unmanaged temporary destination. Initial coverage includes:

- a structured file write or edit below the captured platform temporary root;
- an exact shell redirect below that root;
- the existing exact `WorkingDirectory` and Bash leading-directory cases.

Netclaw will not parse a private executable's options to guess an output path.
An unknown shell operand remains on the normal approval or denial path. The
structured worktree tool owns Git-specific destination semantics.

The correction will not rewrite or execute the original call. The existing
actor-owned intentional-retry key remains. An unchanged retry can reach a
one-time approval when the user truly requires the original path.

The POSIX implementation will cover the observed shared `/tmp` root. Windows
will use the actual host temporary root that Netclaw captures before it injects
the run environment. The change will not add a hard-coded `C:\Windows\Temp`
rule. PII-free live evidence must justify any later Windows pattern.

```text
file_write(Path: "/tmp/result.log")
  -> recoverable_correction(UseManagedTemporaryDirectory, temp_dir)

file_write(Path: "<temp_dir>/result.log")
  -> complete normal authorization
```

**Counterexample:** `diagnostic-tool --output /tmp/result.log` does not prove a
write through shell syntax alone. Netclaw keeps that call on the ordinary
approval path because the option belongs to private executable grammar.

**Counterexample:** `pwd` with `WorkingDirectory=/tmp` performs the exact
directory behavior that the user requested. Netclaw preserves the path because
the call does not author a temporary write.

### Give worktrees a separate managed operation

A worktree is disposable infrastructure, but it can contain valuable source
state. It must not share the ordinary temporary-file contract.

The deferred `worktree_create` tool will:

1. Require an authorized source repository or the current project.
2. Accept a branch name and no arbitrary target directory.
3. Allocate a collision-safe path below `<session-root>/worktrees`.
4. Create the Git worktree through argument-array process execution.
5. Return the exact path and a typed project-scope effect.
6. Record the session and run that own later cleanup.

The actor will apply the project-scope effect only after successful execution.
The tool will not delete an existing directory or worktree. Cleanup remains a
separate capability.

An alternative allowed `git worktree add` through shell policy. Netclaw
rejected that design because safe classification would require private Git
argument grammar.

```text
worktree_create(
  source: current_project,
  branch: "fix/session-temp"
)
  -> path: "<session-root>/worktrees/fix-session-temp-2"
  -> project_scope_effect: use returned path
```

**Counterexample:** The schema does not accept
`destination: "/tmp/fix-session-temp"`. A failed Git operation does not change
project scope and does not delete the destination.

### Use deterministic tests as the primary proof

The runtime change does not depend on model compliance. Contract and
integration tests will prove:

- versioned path creation and recovery;
- parent and child log routing;
- protected raw-log access;
- POSIX and Windows environment values;
- process-local environment isolation;
- correction precedence and retry behavior;
- worktree allocation and project-scope effects;
- legacy resume and rollback compatibility.

The current model evals remain useful, but their role changes.

```text
deterministic tests
  -> prove runtime path, authority, and persistence

model evals
  -> measure whether the agent uses returned paths and tools well

sanitized live traffic
  -> discover new behavior and Windows path patterns
```

The current evals map to the new design as follows:

| Current eval | Action | New assertion |
|---|---|---|
| `subagent_session_scratch_disposable` | Rewrite | A standard temp API returns a path below the child `temp_dir` |
| `approval_session_scratch_disposable` | Rename and rewrite | `file_write` and `file_read` use `<temp_dir>/result.log` without shell |
| `approval_shell_working_directory_argument` | Keep | An explicit read-only `/tmp` cwd remains intact |
| `approval_inline_cd_semantics` | Keep | An exact requested `cd /tmp` remains intact |
| `approval_natural_directory_change` | Keep | A requested process-local directory transition remains intact |

The first two rows replace the old scratch assumption. The final three rows
protect explicit user intent from an overbroad correction.

The old delegated scratch eval will stop requiring
`WorkingDirectory=session_dir`. Its replacement will run a program that uses a
standard temporary API. The assertion will inspect the resulting path and
require it below `temp_dir`.

The old parent disposable-file eval will become
`approval_managed_temp_disposable`. It will keep its useful no-shell assertion,
but it will require `file_write` and `file_read` to use
`<temp_dir>/result.log` instead of `<session_dir>/result.log`.

The eval suite will add these behavioral cases:

- a child uses the injected temporary environment without an export prefix;
- a parent uses first-party file tools under `temp_dir` without a shell call;
- an agent retries an explicit unmanaged POSIX write under `temp_dir` after a
  typed correction;
- an explicit platform-temp task preserves the requested path;
- a parent uses the returned child log reference without a shell search;
- an agent uses `worktree_create` instead of a shell worktree command.

Each pre-change and post-change comparison will use the same prompts, model
configuration, and assertion code. Evidence artifacts will contain no local
user, channel, thread, host, repository, email, token, or secret.

Windows model evals will wait for representative Windows traffic. Native
Windows contract tests remain required in the first implementation.

**Example:** A contract test proves that `TEMP` reaches a Windows child
process. A model eval then measures whether the agent uses that affordance.

**Counterexample:** A model can choose `temp_dir` after it reads prompt text.
That pass does not prove environment injection, access control, or recovery.

## Risks / Trade-offs

- **A session descriptor cannot be persisted.** -> Fail the filesystem action
  before a consumer writes to an unbound location.
- **An old session has files in both log trees.** -> Preserve both paths and
  let the supported inspector read both in time order.
- **A rollback writes a legacy log after version 2 exists.** -> Keep resume
  functional and merge both log lineages after the next upgrade.
- **The model tries to read the audit root.** -> Do not add that root to normal
  workspace or shell authority, and expose only the redacted tool by default.
- **The child-log projection leaks private data.** -> Reuse central redaction,
  omit prompt bodies, apply a strict byte limit, and expose only parent-owned
  child run IDs.
- **A library caches the host temp path before injection.** -> Set the process
  environment before child process creation and test representative SDKs.
- **A model still authors `/tmp`.** -> Return the typed correction when generic
  facts prove the destination. Keep all other calls approval-gated.
- **The worktree contains uncommitted changes when a session ends.** -> Do not
  delete it in this change. Record ownership for a later cleanup policy.
- **The agent-data and audit roots grow without bounds.** -> Keep deletion out
  of this change. Add operator size diagnostics before a cleanup feature.
- **The eval score improves for an unrelated model reason.** -> Treat model
  scores as alignment evidence and use deterministic tests for product claims.

## Migration Plan

1. Add the version-2 two-root descriptor and readers before any writer uses the
   new layout.
2. Make the new binary read version-1 session and log paths.
3. Route only newly bound version-2 sessions to the unified log paths.
4. Add the managed environment and corrections after path recovery passes.
5. Add the child-log and worktree tools after their authority tests pass.
6. Update runbooks and eval assertions before the release.
7. Run a binary-swap test with one legacy session and one new session.

Rollback uses the prior binary without a data migration. It can resume the
session from the existing durable journal and session directory. It can write a
legacy log path. No version-2 file is deleted or moved.

## Open Questions

None. A later cleanup specification will define retention, active-session
leases, quotas, and deletion.
