## Context

See [proposal.md](proposal.md) for the reason for this change. This design uses
the terms in the [engineering glossary](../../../docs/spec/GLOSSARY.md).

Netclaw now computes agent-visible session files below
`NetclawPaths.SessionsDirectory`. It computes raw session logs below the
separate `NetclawPaths.SessionLogsDirectory` tree. This split is the reason a
parent cannot discover all child-owned data through one session lineage.

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

- Give one session one physical storage envelope and one child-run lineage.
- Let agents inspect same-session logs through existing file tools.
- Make standard temporary APIs resolve to a session-owned directory.
- Preserve existing session path behavior without migrating its files.
- Give worktrees a managed location and an explicit owner.
- Keep all authority decisions outside model prose.
- Separate deterministic contract proof from model-alignment evidence.

**Non-Goals:**

- Move the default sessions base below the operating system temporary root.
- Delete a session, artifact, output, temporary file, or worktree.
- Add a quota or retention policy.
- Move an existing session to another configured Netclaw home.
- Grant cross-session log access or log-write authority.
- Claim that an application path boundary is an OS process sandbox.
- Infer private command-line grammar in shell policy.
- Add speculative Windows path patterns without observed evidence.

## Decisions

### Ownership and data lifetime

Each state item has one owner and one lifetime.

| State | Owner | Lifetime | Consumer |
|---|---|---|---|
| Storage binding | Shared session storage resolver | Durable | Ingress, session paths, log dispatcher, child run scope |
| Managed temporary path | Parent or child run scope | Run lifetime; files persist until later cleanup | Process environment and working context |
| Captured host temporary root | Shell approval policy | Daemon process lifetime | Generic path comparison |
| Managed-temp correction key | Parent or child actor | One user turn | Approval bridge |
| Session log paths | Session storage resolver | Durable session lifetime | Existing context assembly, file tools, and `spawn_agent` |
| Session log read scope | Parent or child run scope | Run lifetime | `ScopedFileAccessPolicy` read authorization |
| Session log records | Log dispatcher | Durable file lifetime | Same-session file-tool reads |
| Worktree ownership record | Parent session actor | Durable until later cleanup | Future cleanup policy |

The model does not own any authority state. Model text can request a new call,
but it cannot change a root, grant, or correction key.

### Ordered runtime flow

The following flow is schematic. It omits actor delivery retries and normal
tool authorization stages.

```text
1. The first ingress or actor consumer asks the shared resolver for storage.
2. The resolver returns existing behavior or atomically persists a new binding.
3. An ingress writer, parent actor, or child receives the same resolved paths.
4. A parent or child actor creates an immutable run scope.
5. Run scope derives session_dir, temp_dir, artifact_dir, and raw-log target.
6. Process launcher creates temp_dir and injects TMPDIR, TMP, and TEMP.
7. Tool policy evaluates each authored tool call.
8. Eligible unmanaged-temp writes return UseManagedTemporaryDirectory.
9. The actor commits that correction and arms one turn-local retry key.
10. A replacement call passes complete authorization as a new call.
11. A child run writes its raw log below its child-run directory.
12. `spawn_agent` returns the exact child log path to the parent.
13. Existing context assembly exposes the current run log path.
14. An existing file tool checks the same-session log read scope.
```

Counterexample: A prompt cannot replace steps 1 through 8. A compliant model
does not prove that the runtime injected or enforced these values.

### Bind one physical session storage envelope

Layout version 2 will place all new session-owned files below one persisted
session storage envelope. The session directory is the `workspace/` child of
that envelope, not the envelope itself.

```text
<sessions-base>/<session-id>/             session storage envelope
├── workspace/                            session_dir; default no-project cwd
│   ├── inbox/
│   ├── media/
│   └── tool-calls/
├── artifacts/                            parent retained output
├── tmp/
│   └── parent/                           parent disposable output
├── worktrees/                            managed source worktrees
├── logs/
│   └── session.log                       raw parent log
└── subagents/
    └── <run-id>/                         one child-run directory
        ├── artifacts/
        ├── tmp/
        └── logs/
            └── session.log               raw child log
```

The existing `[session]` context block keeps `session_dir`. It adds
`artifact_dir`, `temp_dir`, and the current run's `log_path` when audience
policy permits exact paths. No second prompt provider or context block owns
these values.

A separate same-session read scope lets existing file tools inspect session
logs. It does not grant log-write, attach, or shell authority. A default
`find .` still starts in `workspace/` and does not recurse into sibling logs.

This is an application authority boundary. It does not prevent an already
authorized arbitrary process that runs as the Netclaw OS identity from opening
a known log path. OS-level containment is a separate capability. Existing file
tools remain the supported model-facing route and avoid shell discovery.

An alternative placed the complete tree below `/tmp/netclaw` or `%TEMP%`.
Netclaw rejected this default because operating system cleanup can remove
attachments that durable turn history still references. An operator can move
the complete Netclaw home through the existing configuration boundary, but
that choice remains subject to the current doctor warning.

**Example:** A parent starts child `run-7`. Its artifacts, temporary files, and
raw log all resolve below `subagents/run-7/` in the parent's envelope.

**Counterexample:** Netclaw does not use the complete envelope as the default
shell cwd. Log read authority does not make the complete envelope a shell safe
root.

### Bind one envelope only for new-layout sessions

One shared session storage resolver will own an optional, immutable storage
binding. Only new-layout sessions have this record. Channel ingress, the main
session actor, child-run creation, and the log dispatcher will all use this
resolver instead of computing paths independently.

```text
SessionStorageBinding
  LayoutVersion
  SessionEnvelopeRoot

ResolvedSessionStorage
  SessionDirectory
  ParentArtifactDirectory
  ParentTemporaryDirectory
  WorktreeDirectory
  ParentRawLogPath
  Child(run_id) -> child artifact, temporary, and raw-log paths
  LogReadScope -> normalized main and child log path classifier
```

Consumers receive `ResolvedSessionStorage`; they do not branch on "legacy" or
"unified" themselves. Only the shared resolver knows whether it used a stored
binding or the unchanged existing-session path rules. This keeps path selection
out of ingress, actor, tool, and logging call sites.

The resolver also supplies the read-only log scope for either path strategy.
The file policy consumes that scope from the existing invocation context. It
does not reconstruct storage paths from current configuration.

The shared resolver will persist the binding before the first new-layout
filesystem side effect. A child will receive it in its immutable run scope. The
child will derive all paths from the parent envelope and its opaque run ID.

The following pseudocode is schematic. It omits persistence retries and actor
message details.

```text
resolve_storage(session_id, configured_paths):
    if binding store has a binding for session_id:
        return paths below its persisted envelope

    if existing session or log data proves a legacy layout:
        return existing session and log path resolvers unchanged

    binding = version 2 envelope below the configured sessions base
    atomically persist binding unless another caller won the race
    return paths below binding.SessionEnvelopeRoot
```

The bound absolute envelope is durable data. A later environment override or
binary upgrade cannot recalculate it. Existing sessions without a binding keep
the current session-directory and session-log resolvers unchanged. The design
does not create a synthetic legacy descriptor or move their files.

The get-or-bind operation must be atomic because channel ingress can write
media before the session actor processes its first message. The first consumer
must not create `inbox/` or `media/` at the envelope root and make a new session
look legacy. Every filesystem helper therefore accepts resolved storage paths;
it does not accept only a session ID plus current configuration.

**Example:** Two concurrent channel messages create the same new session. Both
resolve the same persisted envelope, and both write media below `workspace/`.

**Counterexample:** `ChannelPipeline` does not call
`SessionDirectoryHelper.GetSessionDirectory(sessionId, configuredBase)` and
write a file before storage binding. That would bypass recovery and layout
selection.

An alternative derived every path from the current `NETCLAW_HOME`. That design
would repeat the configuration conflict that caused prior workspace failures.

**Example:** A new session binds `/srv/netclaw-a/sessions/s-42`. An operator
later changes the configured home to `/srv/netclaw-b`. The session still uses
the persisted envelope under `/srv/netclaw-a`.

**Counterexample:** Netclaw does not recompute only the log location from the
new configuration. That would recreate the split lineage this design removes.

### Route session logs by storage binding and child run ID

The log dispatcher will receive a resolved log target. It will not derive a
path from a session ID alone.

```text
main diagnostic with storage binding
  -> persisted envelope
  -> <session-envelope>/logs/session.log

child diagnostic with storage binding
  -> persisted envelope + run id
  -> <session-envelope>/subagents/<run-id>/logs/session.log

legacy diagnostic
  -> existing log resolver
  -> existing legacy target
```

The dispatcher still owns one serialized writer per target. Daemon-global logs
remain under the existing daemon log path.

The `spawn_agent` result will return the child run ID, exact child log path,
and exact child artifact directory. Existing file tools can read, list, and
search same-session logs after session-ownership authorization. They keep their
normal output bounds and pagination.

This design composes existing tools. It does not add a log reader, a new query
language, or a second output contract. The same-session read scope does not
grant log writes, file edits, attachments, or shell authority. Cross-session
log access remains denied.

The complete session is the log-read trust boundary. A parent or child run can
read the main log and every child log in that session. No run can use this
scope to read another session.

The implementation will add this read-only scope to the existing invocation
context and `ScopedFileAccessPolicy`. It will not redefine `{session_dir}` or
add the complete envelope to an audience profile. Public and Team currently
reuse `{session_dir}` across read, write, and attach profiles. Widening that
token would also widen mutation and attachment authority.

For the new layout, the scope accepts only these normalized path shapes:

```text
<session-envelope>/logs/**
<session-envelope>/subagents/<run-id>/logs/**
```

It does not accept `<session-envelope>/**` or
`<session-envelope>/subagents/**`. The latter roots would expose child
artifacts and temporary files. Existing path normalization, link checks, and
protected-path checks still apply.

For an unbound existing session, the same read scope uses the unchanged legacy
log resolver and durable child lineage. It grants access to the resolved main
and child log paths without moving those files.

```text
spawn_agent result
  run_id: "run-7"
  log_path: "/srv/netclaw/sessions/s-42/subagents/run-7/logs/session.log"
  artifact_dir: "/srv/netclaw/sessions/s-42/subagents/run-7/artifacts"

file_read(log_path, StartLine: 1, Limit: 200)
  -> normal bounded line range
```

The log writer keeps an append handle open. `file_read` and `file_search` must
open active logs with a compatible share mode on POSIX and Windows. A read must
not block the writer or fail only because the writer remains active.

`spawn_agent` will return a log path only after the child log target is
resolved and created. The file can be empty, but an immediate authorized read
must not fail because the path is not ready.

**Counterexample:** Netclaw does not add a special session-log reader. The
parent does not need `find`, `grep`, or `cat`. It uses `file_read` on the path.
It can use `file_search` or `file_list` on the path's directory.

**Counterexample:** A path below another session envelope remains denied. A
same-session log read does not let `file_write` modify the log.

**Counterexample:** The implementation does not add the complete `subagents/`
directory as a read root. That path also contains artifacts and temporary
files, which use different access contracts.

**Counterexample:** The implementation does not replace `{session_dir}` with
the envelope root. Public and Team write or attach profiles also consume that
token.

### Give each run one managed temporary environment

The parent path is `<session-envelope>/tmp/parent`. A child path is
`<session-envelope>/subagents/<run-id>/tmp`. Netclaw will create and validate
the directory before it starts a shell or another child process.

Each process receives these process-local values:

```text
TMPDIR = <managed-temp>
TMP    = <managed-temp>
TEMP   = <managed-temp>
```

Netclaw will set all three values on POSIX and Windows. The host process
environment remains unchanged. On Windows, `TMP` and `TEMP` drive native and
.NET temporary-path selection. `TMPDIR` supports cross-platform programs.

The existing model context will keep `session_dir` and add `temp_dir`,
`artifact_dir`, and `log_path` once. It will use one short rule for each path.
The environment remains the primary mechanism. Prompt text is not the security
or correctness boundary.

**Example:** A .NET process calls `Path.GetTempPath()`. The result is the
current run's `temp_dir` because Netclaw injected the environment first.

**Counterexample:** The model does not need to author
`TMPDIR=<temp_dir> command`. Netclaw also does not change the daemon's global
environment for sibling runs.

### Keep the existing session cwd fallback

A shell call with no project and no explicit `WorkingDirectory` will use the
`workspace/` session directory inside the envelope. It will not use the
complete envelope.

Programs that request a temporary path will use `temp_dir` through the injected
environment. The agent does not need to author `cd`, export variables, or add
an environment prefix to each command.

This distinction prevents a no-project conversation from starting at a root
that contains raw logs and child internals. It also prevents temporary SDK
output from mixing with artifacts and inbound files.

**Example:** A no-project shell starts in `<session-envelope>/workspace`. A
library inside that shell creates its cache below `temp_dir` through the
standard environment.

**Counterexample:** Netclaw does not use `temp_dir` as the shell cwd. A final
attachment also does not belong in `temp_dir`; it belongs in `artifact_dir`.

### Retire the session-scratch term by meaning

The current code uses “session scratch” for three different contracts. The
implementation will classify each use before renaming it.

| Current meaning | Replacement term | Representative uses |
|---|---|---|
| Default no-project cwd and relative-path base | session directory; `session_dir` | `ToolExecutionContext.SessionDirectory`, file-tool schemas, cwd fallback |
| Disposable run-local files | managed temporary directory; `temp_dir` | platform-temp correction, model guidance, process environment |
| Session-specific directory unsuitable for a durable folder grant | session-owned directory | approval-option pruning and runbooks |

The correction pipeline will use the second meaning. It will replace
`UseSessionScratch` with `UseManagedTemporaryDirectory`, and its trusted path
will be the run's exact `temp_dir`, not `session_dir`. Correction, retry,
approval-context, parent-actor, and child-actor type names will use
`ManagedTemporaryDirectory` or `ManagedTemp` consistently.

File tools use the first meaning. Their schemas will say that relative paths
resolve against the current project and then the session directory. They will
not claim that the session directory is disposable scratch.

Approval-option pruning uses the third meaning. The implementation will state
which session-owned directories suppress a reusable folder grant. It will not
carry the name “session scratch” after the paths have distinct purposes.

One persisted approval field currently stores `session_scratch_directory` at
protobuf field 19. The implementation will retain that field as legacy-read-only
input and add a new `managed_temporary_directory` field with a new field number.
New events will not write field 19. The runtime will not reinterpret an old
session-directory path as `temp_dir`. When an old pending approval resumes, it
will derive the current managed temporary directory from resolved run storage
or omit the new correction metadata.

```text
old pending approval
  session_scratch_directory = <old session_dir>
  -> never relabel that value as temp_dir
  -> derive current temp_dir from resolved run storage, or omit temp guidance

new correction receipt
  remediation = UseManagedTemporaryDirectory
  managed_temporary_directory = <current run temp_dir>
```

Archived OpenSpec changes and immutable evidence remain historical records.
Current production code, active specifications, tool schemas, runtime prompts,
runbooks, and tests will use the new vocabulary.

The initial source audit identifies these implementation groups:

| Area | Current anchors | Required change |
|---|---|---|
| Remediation identity and text | `ToolRemediationCode`, `ToolRemediationPresenter`, `ToolOutcomeResults` | Replace `UseSessionScratch`; render `temp_dir` as the managed temporary directory |
| Platform-temp detection | `PlatformTemporaryScopePolicy` | Replace `SessionScratchSuggested` and carry the resolved run `temp_dir` |
| Retry and approval flow | `IToolExecutor`, `DispatchingToolExecutor`, `ToolAccessPolicy`, `SessionToolExecutionPipeline` | Rename correction state and keys; compare retries against managed-temp semantics |
| Parent and child actors | `LlmSessionActor`, `SubAgentActor`, `LlmMessages` | Use the same managed-temp correction and lifecycle state in both paths |
| Durable pending approvals | `ToolApprovalState`, `SessionProtocol.Events`, `netclaw_messages.proto`, `NetclawProtoMapper` | Add the new field, retain field 19 as legacy-read-only, and test recovery without reinterpretation |
| Model-visible guidance | shipped `AGENTS.md`, `SessionMessageAssembler`, `SubAgentActor`, `ToolChoiceGuidance`, `ShellTool` | Extend the existing session block with `temp_dir`, `artifact_dir`, and `log_path`; preserve current `session_dir` assembly |
| Workspace file schemas | `FileReadTool`, `FileListTool`, `FileSearchTool`, `FileWriteTool`, `FileEditTool`, `AttachFileTool` | Replace “session scratch” with “session directory” for relative-path fallback |
| Session log reads | `ToolExecutionContext`, `ScopedFileAccessPolicy`, `FileReadTool`, `FileSearchTool`, `SessionLogActor` | Add a read-only same-session log scope and use a writer-compatible file share mode |
| Approval scopes and prompts | `ApprovalBucketBuilder`, `ToolAccessPolicy`, Slack and Discord approval builders, approval runbook | Name the broader rule “session-owned directory” and define which roots suppress persistent folder grants |
| Verification | actor, policy, serialization, approval-rehydration, TUI, configuration, and daemon test projects | Rename fixtures and add distinct `session_dir` versus `temp_dir` assertions |

**Counterexample:** A global replacement from `SessionScratchDirectory` to
`ManagedTemporaryDirectory` would make a persisted `session_dir` appear to be
the new disposable directory. That is a semantic migration bug, not a rename.

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
3. Allocate a collision-safe path below `<session-envelope>/worktrees`.
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
  -> path: "<session-envelope>/worktrees/fix-session-temp-2"
  -> project_scope_effect: use returned path
```

**Counterexample:** The schema does not accept
`destination: "/tmp/fix-session-temp"`. A failed Git operation does not change
project scope and does not delete the destination.

### Use deterministic tests as the primary proof

The runtime change does not depend on model compliance. Contract and
integration tests will prove:

- versioned single-envelope path creation and recovery;
- parent and child log routing;
- same-session log reads and cross-session log denial;
- separation between log-read, log-write, and shell authority;
- live log reads that do not block the writer on POSIX or Windows;
- legacy main and child log reads without file migration;
- POSIX and Windows environment values;
- process-local environment isolation;
- correction precedence and retry behavior;
- worktree allocation and project-scope effects;
- existing-session resume and new-session recovery.

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
- a parent uses the returned child log path with existing file tools;
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

- **A session binding cannot be persisted.** -> Fail the filesystem action
  before a consumer writes to an unbound location.
- **An old session has separate session and log trees.** -> Leave its storage
  binding absent and keep the existing resolvers unchanged.
- **A rollback cannot understand the new binding.** -> Keep pre-feature binary
  support for new-layout sessions out of scope. Existing sessions remain
  compatible because their paths do not move.
- **The model tries to read a same-session log.** -> Return the exact path and
  authorize existing file-read, file-list, and file-search operations.
- **The model tries to change a log.** -> Keep write and edit authority separate
  from the same-session log read scope.
- **The model tries to read a foreign session log.** -> Deny the operation
  without revealing whether the foreign file exists.
- **A file tool reads while the log writer stays open.** -> Use a compatible
  read share mode and test the active writer on Windows and POSIX.
- **An implementation widens `{session_dir}` to the envelope.** -> Keep the
  existing token unchanged and add a read-only log scope to invocation context.
- **An approved arbitrary process knows a raw-log path.** -> Treat this change
  as an application authority boundary, not an OS sandbox. Add process
  containment only through a separate security design.
- **A library caches the host temp path before injection.** -> Set the process
  environment before child process creation and test representative SDKs.
- **A model still authors `/tmp`.** -> Return the typed correction when generic
  facts prove the destination. Keep all other calls approval-gated.
- **The worktree contains uncommitted changes when a session ends.** -> Do not
  delete it in this change. Record ownership for a later cleanup policy.
- **The session envelope grows without bounds.** -> Keep deletion out of this
  change. Add operator size diagnostics before a cleanup feature.
- **The eval score improves for an unrelated model reason.** -> Treat model
  scores as alignment evidence and use deterministic tests for product claims.

## Migration Plan

1. Add the optional versioned storage binding before any writer uses the new
   layout.
2. Keep the binding absent for existing sessions and leave their current
   session and log resolvers unchanged. Do not move or copy their data.
3. Route only newly bound sessions into one physical envelope.
4. Add the managed environment and corrections after path recovery passes.
5. Add same-session log read scope and exact child paths after authority tests
   pass.
6. Add the worktree tool after its authority tests pass.
7. Update runbooks and eval assertions before the release.
8. Upgrade one existing session and restart one newly bound session.

Existing-session compatibility is intentionally narrow: a current binary keeps
each unbound existing session on its established directories. A pre-feature
binary resuming a newly bound session is out of scope. No upgrade path moves or
deletes session files.

## Open Questions

None. A later cleanup specification will define retention, active-session
leases, quotas, and deletion.
