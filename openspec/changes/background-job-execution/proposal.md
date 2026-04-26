## Why

Long-running commands (full test suites, headless Claude Code sessions, large
builds) cannot run through the synchronous tool execution pipeline without
blocking the session for the entire duration. At 75+ minutes for something
like `dotnet test` on a large project, the session would idle-timeout,
passivate, or simply hold resources doing nothing. The existing workaround
(`nohup ... &` followed by a separate file read) is clumsy and requires two
tool calls. Background job execution provides a first-class async model:
kick off the process, get a handle back immediately, session can passivate or
continue other work, and results are delivered via the existing reminder
re-entrancy channel when the process completes.

Depends on: `structured-tool-call-metadata` change (provides the
`ToolCallMeta` envelope with explicit `_background` signaling and
`_timeout_seconds` synchronous timeout hints).

## What Changes

- Introduce `BackgroundJobManagerActor` as an infrastructure-level singleton
  (sibling to `ReminderManagerActor`) that owns background job lifecycle
  independently of any session.
- Per-job `BackgroundJobExecutionActor` child actors spawn and monitor
  processes, capture output to disk, and deliver results to the originating
  session via `DeliverTrustedSessionTurn` on completion.
- `SessionToolExecutionPipeline` routes tool calls to background execution
  only when `_background` is true.
- `SessionState` gains an `ActiveBackgroundJobs` collection (persisted to
  journal) so the LLM knows what it's waiting for after compaction or
  session resumption.
- New `check_background_job` tool is exposed only when shell execution is
  available, and lets the LLM query status or cancel running jobs. Background
  jobs remain a shell-only surface coupled to shell grants/availability.
- Job definitions/history persist to disk (`~/.netclaw/jobs/{id}.json`) for
  startup reconciliation and diagnostics. If the daemon restarts or goes down
  mid-job, in-flight jobs are best-effort and may need to be relaunched.

## Capabilities

### New Capabilities
- `background-job-execution`: Infrastructure-level async job execution with
  process monitoring, output capture, session re-entrancy on completion,
  job status/cancellation tooling, and session state tracking.

### Modified Capabilities
- `netclaw-session`: `SessionState` gains `ActiveBackgroundJobs` persisted
  dictionary; working context surfaces pending jobs to the LLM.
- `netclaw-scheduling`: Shares the `DeliverTrustedSessionTurn` re-entrancy
  pattern and delivery observation contract. No requirement changes — just
  pattern reuse.

## Impact

- **Code**: New `Netclaw.Actors/Jobs/` directory with manager actor, execution
  actor, protocol types, and tool. Modifications to `SessionToolExecutionPipeline`,
  `SessionState`, `LlmSessionActor`, and `Program.cs` (DI registration).
- **Persistence**: Additive field on `SessionState` for active jobs. Job
  definitions/history stored as JSON files on disk for reconciliation and
  diagnostics (same pattern as reminders), not durable execution continuity.
- **Security**: `check_background_job` is available only when shell execution
  is available and shares the `shell` grant category, inheriting Personal-only
  audience restriction. Jobs carry the originating
  session's audience and boundary for delivery. Completion remains trusted by
  design to match normal synchronous shell results, but only within the
  originating session and stored originating audience/boundary.
- **Infrastructure**: New singleton actor registered at daemon startup.
  Concurrency limit (default 5) prevents resource exhaustion.
- **Dependencies**: Reuses `DeliverTrustedSessionTurn`, delivery observation,
  and `ShellTool`'s process management patterns. No new external dependencies.
