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
- Make the current session envelope an implicit trusted root for parent and
  child runs.
- Let agents inspect authorized session data and ordinary configuration through
  existing file tools.
- Make standard temporary APIs resolve to the managed temporary directory.
- Preserve existing session path behavior without migrating its files.
- Give worktrees a managed location and compose creation from existing tools.
- Keep all authority decisions outside model prose.
- Separate deterministic contract proof from model-alignment evidence.

**Non-Goals:**

- Move the default sessions base below the operating system temporary root.
- Delete a session, artifact, output, temporary file, or worktree.
- Add a quota or retention policy.
- Move an existing session to another configured Netclaw home.
- Add special per-log, per-child, or foreign-session filesystem authority.
- Claim that an application path boundary is an OS process sandbox.
- Infer private command-line grammar in shell policy.
- Store secret values in `netclaw.json` or make secret stores agent-readable.
- Add a special configuration-reader or worktree tool.
- Add speculative Windows path patterns without observed evidence.

## Decisions

### Ownership and data lifetime

Each state item has one owner and one lifetime.

| State | Owner | Lifetime | Consumer |
|---|---|---|---|
| Storage binding | Shared session storage resolver | Durable | Ingress, session paths, log dispatcher, child run scope |
| Managed temporary path | Parent or child run scope | Run lifetime; files persist until later cleanup | Process environment and working context |
| Captured host temporary root | Managed-temp remediation policy | Daemon process lifetime | Temporary-destination comparison |
| Managed-temp correction key | Parent or child actor | One user turn | Approval bridge |
| Session log paths | Session storage resolver | Durable session lifetime | Existing context assembly, file tools, and `spawn_agent` |
| Session log records | Log dispatcher | Durable file lifetime | Same-session file-tool reads |

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
5. Run scope derives session_dir, temp_dir, artifact_dir, worktree_dir, and raw-log target.
6. Process launcher creates temp_dir and injects TMPDIR, TMP, and TEMP.
7. Tool policy evaluates each authored tool call.
8. Eligible unmanaged-temp writes return UseManagedTemporaryDirectory.
9. The actor commits that correction and arms one turn-local retry key.
10. A replacement call passes complete authorization as a new call.
11. A child run writes its raw log below its child-run directory.
12. `spawn_agent` returns the exact child log and artifact paths to the parent.
13. Existing context assembly exposes the current run log path.
14. An existing file tool checks the current session and inherited trusted roots.
```

Counterexample: A prompt cannot replace steps 1 through 8. A compliant model
does not prove that the runtime injected or enforced these values.

### Consolidate filesystem path decisions

The current implementation uses competing terms and policy helpers for the
same filesystem questions. The final implementation will use one vocabulary
and one shared path decision contract.

The contract separates these ordered responsibilities:

| Responsibility | Input | Output |
|---|---|---|
| Tool capability | Tool name, audience, and grant profile | Whether the invocation can enter its tool-family policy |
| Tool-family safety | Shell mode and parsed command facts, or the structured tool contract | Whether family-specific terminal policy admits the invocation |
| Path facts | A raw path | Its canonical path, link state, and relationship to a trusted root |
| File protection | Path facts, operation, audience, and trusted roots | A typed allow or deny path access decision with a reason |
| Approval | An invocation that passed all terminal policy layers | Stored, one-time, or requested user authority |
| Remediation | A request that passed terminal authorization checks and would otherwise require approval, plus trusted runtime guidance | Optional advice that grants no authority |

These layers compose as nested gates. Tool capability runs before shell command
policy. Shell command policy runs before shell file protection. File protection
runs before approval. A terminal denial stops the pipeline and cannot be widened
by a later layer.

Structured tools provide their exact file operation to file protection. Shell
commands conservatively request `Write` for every known referenced path because
Netclaw does not infer whether arbitrary shell syntax will read or mutate it.
File authority has a one-way dependency: it can deny an otherwise eligible
shell call, but it cannot grant shell capability or bypass shell policy.

Shell file protection derives real path facts from `ShellCommandAnalysis`.
It does not depend on reusable approval candidates. Dynamic syntax can remove
grant candidates, but it cannot hide known paths from file protection. A later
causal projection adds intent and fallback views. The coordinator checks those
views before it checks stored grants or reviewed-safe coverage.

When the canonical parser has already applied shell consumer semantics, file
protection uses its resolved path fact. For example, PowerShell resolves
`FileSystem::C:\work\input.txt` to the filesystem path before Netclaw applies
`Write` protection. Netclaw does not repeat provider parsing or add a rule for
`Get-Content`.

Each capability has one policy owner:

| Capability | Owner | Excluded responsibility |
|---|---|---|
| Filesystem authorization | `netclaw-tools` | Session layout and approval presentation |
| Trusted sessions root and named directories | `netclaw-session` | File-operation authorization |
| Working and managed temporary directory selection | `session-cwd` | Common path access decision |
| Approval and remediation after authorization | `tool-approval-gates` | Path normalization and containment |

Other specifications SHALL reference the owning requirement. They SHALL NOT
repeat its policy with different terms or another decision path.

The source of a path does not create another safety model. Structured file
arguments, project-directory declarations, shell path facts, and reviewed-safe
shell candidates use the same path facts and path access decision contract.
Interactive approval does not widen an explicit `Roots` or `None` file profile.
An unresolved interactive shell path can still use the existing one-shot
approval path, but it cannot receive reviewed-safe or persistent coverage.

The target glossary uses these terms:

- **Canonical path:** A normalized absolute path. Canonical form grants no
  authority.
- **Trusted root:** A directory boundary that an invocation can use for a
  specified operation.
- **Path relationship:** Whether the canonical path is the trusted root, is a
  descendant, or is outside it.
- **File operation:** The requested read, list, search, write, edit, attach,
  working-directory, or execution use.
- **Path access decision:** A typed allow or deny result. It carries the
  canonical path when resolution succeeds; a denial also carries a failure
  category and human-readable detail.

The final edit will condense or remove duplicate requirements, scenarios,
helpers, and tests. It will not preserve an old abstraction only to avoid a
mechanical change.

The Netclaw sessions directory is a trusted root for every parent and child
run. All session directories are below this root and are accessible to other
sessions under normal audience and operation permissions. This design lets
one session analyze another session's logs, which supports diagnosis on a
heavily used Netclaw agent.

```text
tool exposure and audience capability
  -> tool-family safety
  -> canonical path and trusted-root relationship, when applicable
  -> exact file operation for structured tools, conservative Write for shell
  -> deny terminally
  -> or pass terminal checks, then approval or execution
```

**Counterexample:** Session identity does not create a separate access-control
list. The shared sessions root provides containment, while audience and
operation permissions still control the requested action.

**Counterexample:** Temporary-path detection does not produce or override a path
access decision. After terminal path and shell-policy checks pass, it may use
path and syntax facts to propose `temp_dir` before the call would otherwise
prompt for approval. The replacement call is authorized from the beginning. A
denied call receives no remediation that could be mistaken for authority.

**Counterexample:** A correction or approval cannot turn denied file access into
an allowed shell call. It can only act after capability, shell policy, and file
protection have admitted the request.

### Bind one physical session storage envelope

Layout version 2 will place all new session-owned files below one persisted
session storage envelope. The session directory is the `workspace/` child of
that envelope, not the envelope itself.

```text
<sessions-base>/<session-id>/             session storage envelope
├── attachment-staging/                   untrusted inbound bytes before admission
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

The attachment staging directory is inside the envelope but outside
`workspace/`. Its location does not make its contents trusted. The existing
attachment pipeline scans each staged file before it moves an accepted file
to `workspace/inbox/`. A rejected file never becomes agent-visible media.

The existing `[session]` context block keeps `session_dir`. It adds
`artifact_dir`, `temp_dir`, `worktree_dir`, and the current run's `log_path`
when audience policy permits exact paths. No second prompt provider or context
block owns these values.

The complete current envelope is an implicit trusted root for existing tools.
Parent and child runs also inherit configured trusted roots. Existing audience
and operation permissions still decide reads, writes, edits, attachments, and
shell execution. A default `find .` still starts in `workspace/` and does not
recurse into sibling logs.

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
shell cwd. A trusted-root relationship also does not bypass shell syntax,
hard-deny, or approval policy.

### Bind one envelope only for new-layout sessions

One shared session storage resolver will own an optional, immutable storage
binding. Only new-layout sessions have this record. Channel ingress, the main
session actor, child-run creation, and the log dispatcher will all use this
resolver instead of computing paths independently.

Each storage location has a distinct value-object type. This keeps a workspace,
attachment-staging directory, artifact directory, managed temporary directory,
worktree directory, and log path from being exchanged as unlabelled strings.
The underlying string is used only at filesystem and database boundaries.

```text
SessionStoragePaths
  Binding: SessionStorageBinding?
    LayoutVersion: SessionStorageLayoutVersion
    EnvelopeRoot: SessionStorageEnvelopeRoot
  SessionDirectory: SessionWorkspaceDirectory
  AttachmentStagingDirectory: AttachmentStagingDirectory
  ArtifactDirectory: ArtifactDirectory
  ManagedTemporary: ManagedTemporaryLocation
  WorktreeDirectory: WorktreeDirectory
  LogPath: SessionLogPath
  ForChild(run_id) -> child artifact, temporary, and raw-log paths
```

Consumers receive `SessionStoragePaths`; they do not branch on "legacy" or
"unified" themselves. Only the shared resolver knows whether it used a stored
binding or the unchanged existing-session path rules. This keeps path selection
out of ingress, actor, tool, and logging call sites.

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

The physical envelope name must include a collision-resistant identity. A
lossy display-safe session ID is not sufficient.

```text
raw session A: channel/a_b  -> display-safe channel_a_b
raw session B: channel/a/b  -> display-safe channel_a_b

invalid: both bind <sessions-base>/channel_a_b
valid:   each binding records a distinct collision-resistant root
```

The bound absolute envelope is durable data. A later environment override or
binary upgrade cannot recalculate it. Existing sessions without a binding use
the current legacy session-directory and session-log resolvers. The design
does not create a synthetic legacy descriptor or move their files. If an
operator changes a legacy root, files below the old root remain there and can
fall outside current discovery.

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

Legacy detection must query the persistence schema that the product actually
ships. A journal-only session with no snapshot is still an existing session.
Using a stale table name would misclassify it as new and create a second
layout.

Netclaw has exactly one SQLite database at `NetclawPaths.SqliteDbPath`. It is
the source of truth for every SQLite-backed production feature: actor journal
and snapshot data, durable reminders, the session catalog, daily statistics,
memory, and storage bindings. The production configuration cannot select a
different database or an in-memory persistence provider. A supplied
`Persistence` section fails configuration validation and daemon startup instead
of being silently ignored.

The resolver maps one row from the Netclaw database to one storage-state
object. It does not inspect table or column shapes at runtime. Startup
migrations own schema compatibility.

**Example:** A live daemon records a session event, snapshot, reminder, catalog
entry, daily statistic, memory, and storage binding in the same `netclaw.db`.

**Counterexample:** Netclaw does not open a second database for memory or
control data. In-memory persistence used inside a test harness is not a runtime
configuration option and does not change the production storage contract.

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

The first storage bind must remain atomic, but an already resolved immutable
binding does not need a SQLite immediate transaction for every log record. The
dispatcher or run scope should carry or cache the resolved target after first
resolution. This avoids turning high-volume logging into repeated write-lock
traffic.

The `spawn_agent` result will return the child run ID, exact child log path,
and exact child artifact directory. Existing file tools keep their normal
output bounds and pagination.

The shared `netclaw-tools` path access decision authorizes these paths. The
Netclaw sessions root covers all new session envelopes. The legacy log root
remains available while legacy sessions exist.

This design adds no log reader, query language, ownership list, or output
contract. One session can inspect another session's log when its audience and
requested file operation permit access.

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

**Example:** One Personal session uses `file_read` to diagnose another
session's log below the shared sessions root.

**Counterexample:** The implementation does not replace `{session_dir}` with
the sessions root. That token remains the relative-path base.

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
`artifact_dir`, `worktree_dir`, and `log_path` once. It will use one short rule
for each path. The environment remains the primary temporary-file mechanism.
Prompt text is not the security or correctness boundary.

**Example:** A .NET process calls `Path.GetTempPath()`. The result is the
current run's `temp_dir` because Netclaw injected the environment first.

**Counterexample:** The model does not need to author
`TMPDIR=<temp_dir> command`. Netclaw also does not change the daemon's global
environment for sibling runs.

Persisted background-job JSON written before this change has no
`ManagedTemporaryDirectory` property. The loader must continue to deserialize
that shape. This change does not invent resume behavior for an OS process that
was lost during restart. Existing background-job semantics remain authoritative:
terminal history remains available, and a previously pending or running job
becomes `Lost` and notifies its owning session. It must not resume with host
temporary storage as a silent fallback.

**Counterexample:** A missing managed-temp property does not make the complete
background-job record unreadable and does not delete its captured output.

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
| Session storage path unsuitable for a durable folder grant | exact named storage path | approval-option pruning and runbooks |

The correction pipeline will use the second meaning. It will replace
`UseSessionScratch` with `UseManagedTemporaryDirectory`. Its canonical
remediation destination will be the run's exact `temp_dir`, not `session_dir`. Correction, retry,
approval-context, parent-actor, and child-actor type names will use
`ManagedTemporaryDirectory` or `ManagedTemp` consistently.

File tools use the first meaning. Their schemas will say that relative paths
resolve against the current project and then the session directory. They will
not claim that the session directory is disposable scratch.

Approval-option pruning uses the third meaning. The implementation will name
which storage paths suppress a reusable folder grant. It will not create
another filesystem authority term.

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
| Model-visible guidance | shipped `AGENTS.md`, `SessionMessageAssembler`, `SubAgentActor`, `ToolChoiceGuidance`, `ShellTool` | Extend the existing session block with `temp_dir`, `artifact_dir`, `worktree_dir`, and `log_path`; preserve current `session_dir` assembly |
| Workspace file schemas | `FileReadTool`, `FileListTool`, `FileSearchTool`, `FileWriteTool`, `FileEditTool`, `AttachFileTool` | Replace “session scratch” with “session directory” for relative-path fallback |
| Session data access | `ToolExecutionContext`, `PathAccessPolicy`, `FileReadTool`, `FileSearchTool`, `SessionLogActor` | Use the shared sessions root and one path access decision; use a writer-compatible file share mode |
| Ordinary config reads | `ToolPathPolicy`, `DaemonToolPathPolicyFactory`, configuration persistence and validation | Separate structured read denies from broad shell indicators; allow validated `netclaw.json`; keep secret and control-plane files denied |
| Background-job recovery | `BackgroundJobDefinitionStore`, `BackgroundJobManagerActor` | Read records without managed-temp metadata; keep existing `Lost` transition and notification semantics |
| Approval scopes and prompts | `ApprovalBucketBuilder`, `ToolAccessPolicy`, Slack and Discord approval builders, approval runbook | Name each storage path that suppresses a persistent folder grant |
| Verification | actor, policy, serialization, approval-rehydration, TUI, configuration, and daemon test projects | Rename fixtures and add distinct `session_dir` versus `temp_dir` assertions |

**Counterexample:** A global replacement from `SessionScratchDirectory` to
`ManagedTemporaryDirectory` would make a persisted `session_dir` appear to be
the new disposable directory. That is a semantic migration bug, not a rename.

### Extend the existing correction with one managed-temp code

`UseManagedTemporaryDirectory` will be a closed remediation code. The
remediation presenter will name the exact managed path from trusted invocation
context.

The correction applies only when Netclaw has a generic fact that proves the
agent authored an unmanaged temporary destination. Initial coverage includes:

- a structured file write or edit below the captured platform temporary root;
- an exact shell redirect below that root;
- the existing exact `WorkingDirectory` and Bash leading-directory cases.

Netclaw will not parse a private executable's options to guess an output path.
An unknown shell operand remains on the normal approval or denial path.

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

### Compose worktrees from shell and project-scope tools

A worktree is disposable infrastructure, but it can contain valuable source
state. It must not share the ordinary temporary-file contract.

The session context will announce the exact `worktree_dir` at
`<session-envelope>/worktrees`. The agent will use `shell_execute` to run Git
and then use `set_working_directory` after creation succeeds. Normal shell
authorization applies. Worktree authority is not a parallel permission model;
it is the ordinary shell authority for a destination inside the current
session root. That root fact does not bypass shell syntax, hard-deny, or
approval policy.

This design adds no `worktree_create` tool, destination allocator,
worktree-specific authorization model, typed worktree effect, or durable
ownership record. Those parts have no cleanup consumer in this change. Git
already owns worktree semantics, and existing tools can compose the desired
workflow.

```text
session context
  worktree_dir: "/srv/netclaw/sessions/s-42/worktrees"

shell_execute(
  command: "git worktree add /srv/netclaw/sessions/s-42/worktrees/fix-session-temp fix/session-temp"
)
  -> success

set_working_directory(
  path: "/srv/netclaw/sessions/s-42/worktrees/fix-session-temp"
)
  -> project scope adopted
```

**Counterexample:** A failed Git command does not change project scope. The
agent must not call `set_working_directory` as though the worktree exists.

**Counterexample:** Netclaw does not inspect Git option grammar to auto-approve
the command. If an agent instead chooses `/tmp/fix-session-temp`, ordinary
shell policy evaluates the exact call.

A targeted correction for a statically visible `git worktree add` destination
below host temp is a deferred alternative. It is not part of this change.
Netclaw will add it only if evals and sanitized live traffic show that
`worktree_dir` context plus normal shell behavior is insufficient.

### Let structured reads inspect ordinary configuration

The current path policy uses one broad configuration-directory shell indicator
as part of structured read denial. This blocks `file_read(netclaw.json)` even
though the file is ordinary operator configuration. Shell and structured file
tools have different certainty: a file tool supplies one exact path and
operation, while arbitrary shell text can combine reads, writes, and globbing.

The read policy will therefore be independent from the broader shell policy.
An exact structured read of `netclaw.json` follows normal trusted-root and
audience rules. Writes, edits, attachments, and shell calls keep their
independent policy. Netclaw will not add a special redacted-config tool.

```text
file_read("<netclaw-home>/config/netclaw.json")
  -> normal root check
  -> normal audience and read-permission check
  -> bounded ordinary configuration

file_read("<netclaw-home>/config/secrets.json")
  -> protected-path denial
```

`netclaw.json` has no secret-bearing fields. API keys, OAuth credentials,
secret headers, webhook secret material, and similar credentials use separate
protected stores. This change relies on that existing configuration contract.
It does not add migration, redaction, or secret-field classification.

**Example:** An authorized agent reads `Workspaces.Directory` from
`netclaw.json` to understand why one path is selected.

**Counterexample:** The agent cannot read `secrets.json` or SQLite state merely
because the configuration directory is under a trusted root.

**Counterexample:** Read access does not authorize the agent to rewrite
`netclaw.json`. Existing self-configuration policy remains separate.

**Counterexample:** `netclaw.json` is one persisted configuration layer. If an
environment variable overrides `Workspaces.Directory`, reading the file does
not claim to show the effective value or its source. Configuration provenance
belongs to the separate `netclaw config` design.

### Use deterministic tests as the primary proof

The runtime change does not depend on model compliance. Contract and
integration tests will prove:

- versioned single-envelope path creation and recovery;
- collision resistance for raw session identifiers with equal sanitized forms;
- parent and child log routing;
- current-session and inherited trusted-root authority;
- separation between audience read, write, attach, and shell permissions;
- live log reads that do not block the writer on POSIX or Windows;
- legacy main and child log reads without file migration;
- journal-only legacy session detection against the shipped schema;
- POSIX and Windows environment values;
- process-local environment isolation;
- recovery of background-job JSON without managed-temp metadata;
- correction precedence and retry behavior;
- readable ordinary `netclaw.json` with protected stores still denied;
- link and reparse-point checks that include the trusted root itself;
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
- an explicit platform-temp task preserves the requested path;
- a parent uses the returned child log path with existing file tools;
- an agent uses `shell_execute` to create a worktree below `worktree_dir` and
  adopts it with `set_working_directory`.

Only an unchanged prompt, tool surface, model configuration, and assertion can
support a direct pre-change and post-change comparison. A rewritten scratch or
worktree case is replacement behavioral evidence and must be labeled as such.
Evidence artifacts will contain no local user, channel, thread, host,
repository, email, token, secret, or private model or hardware identifier.

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
  binding absent and use the current legacy resolvers. Do not move its files.
- **A rollback cannot understand the new binding.** -> Keep pre-feature binary
  support for new-layout sessions out of scope. Existing sessions remain
  compatible because their paths do not move.
- **The model tries to read a session log.** -> Return the exact path and apply
  the shared path access decision.
- **The model tries to change a log.** -> Apply ordinary write and edit policy;
  read permission is not write permission.
- **The model tries to read another session.** -> Use the shared sessions root,
  audience policy, and requested file operation.
- **A file tool reads while the log writer stays open.** -> Use a compatible
  read share mode and test the active writer on Windows and POSIX.
- **An implementation widens `{session_dir}` to the sessions root.** -> Keep
  the token as the relative-path base and supply authority separately.
- **An approved arbitrary process knows a raw-log path.** -> Treat this change
  as an application authority boundary, not an OS sandbox. Add process
  containment only through a separate security design.
- **A library caches the host temp path before injection.** -> Set the process
  environment before child process creation and test representative SDKs.
- **A model still authors `/tmp`.** -> Return the typed correction when generic
  facts prove the destination. Keep all other calls approval-gated.
- **The worktree contains uncommitted changes when a session ends.** -> Do not
  delete it in this change. Let Git and the operator retain the state.
- **An operator puts a secret in an ordinary field.** -> Treat this as an
  operator configuration error. This change does not add content inspection.
- **A broad shell indicator blocks ordinary configuration reads.** -> Keep
  structured file-read denies independent from shell indicators.
- **The session envelope grows without bounds.** -> Keep deletion out of this
  change. Add operator size diagnostics before a cleanup feature.
- **The eval score improves for an unrelated model reason.** -> Treat model
  scores as alignment evidence and use deterministic tests for product claims.

## Migration Plan

1. Add the optional versioned storage binding before any writer uses the new
   layout.
2. Keep the binding absent for existing sessions and use the current legacy
   session and log resolvers. Do not move or copy their data.
3. Route only newly bound sessions into one physical envelope.
4. Add the managed environment and corrections after path recovery passes.
5. Add current-session and inherited trusted roots plus exact child paths after
   authority tests pass.
6. Expose `worktree_dir`; remove the custom worktree tool; validate the composed
   Git and project-scope workflow.
7. Separate exact structured-read policy from broad shell indicators and allow
   ordinary `netclaw.json` reads.
8. Update runbooks and eval assertions before the release.
9. Upgrade one existing session and restart one newly bound session.

Existing-session compatibility is intentionally narrow. A current binary does
not migrate files for an unbound session. A root configuration change can
leave those files outside current discovery. A pre-feature binary that resumes
a newly bound session is out of scope. No upgrade path moves or deletes session
files.

## Open Questions

None. A later cleanup specification will define retention, active-session
leases, quotas, and deletion.
