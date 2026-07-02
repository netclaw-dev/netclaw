# Proposal: Background Jobs as Detached Processes with No Completion Expectation

## Why

A production session (`D0AC6CKBK5K_1778604339_455639`) hung trying to launch a Jekyll dev server to validate website changes: foreground `shell_execute` blocks until process exit, and background jobs block on EOF + `WaitForExitAsync` before reporting — so a process that runs indefinitely (dev server, watcher) can never be used by the agent. Investigation also found that background jobs silently receive a 60-second default kill timer (contradicting their own documentation), write their output log only at process exit (so the documented `file_read`/`grep` monitoring path is dead while the job runs), and are silently marked `Lost` on daemon restart without telling the owning session.

## What Changes

- **Background jobs become detached processes with no completion expectation.** Process exit (success, failure, timeout, cancel) is a notification event, not a requirement of the job model. One unified path — no separate "server mode," no new tool schema.
- **Output streams to the job log while the process runs.** stdout/stderr are pumped line-by-line (with per-line secret redaction) to `~/.netclaw/jobs/{id}/output.log` with bounded rotation, instead of a single write at exit. The existing `check_background_job` tail query and `file_read`/`grep` monitoring paths work mid-run. Output survives daemon crashes.
- **BREAKING** — **No default kill timer.** Omitted `_timeout_seconds` means the job runs until it exits, is cancelled, or its session passivates (previously: silent 60s default; non-positive hints are still normalized away by loud-validation extraction). Positive `_timeout_seconds` remains an opt-in kill timer.
- **BREAKING** — **Jobs are reaped on session passivation.** When the owning session passivates after idle timeout, its running jobs are killed (new `Reaped` status) via a handshake before the final snapshot. No turn delivery on reap (it would rehydrate the session being torn down); the agent sees the reaped entry in `[active-background-jobs]` on next rehydration. This replaces the prior "jobs outlive passivation" model; the long-job-while-idle use case is consciously traded away (documented alternatives: check-back reminders, scheduled tasks).
- **Lost jobs notify their session.** Daemon-restart reconciliation delivers the standard termination notification (status `Lost`, log path) to owning sessions instead of only logging. Bounded by design: passivated sessions have no live jobs, so only warm sessions' jobs exist at restart.
- **Submit ACK and status responses include the output log path** so the agent can monitor without an extra query.
- **Pending approvals no longer defer idle passivation.** Approval state is journaled and the resume-after-passivation path already exists (`session-resume` spec); the deferral predates approval persistence and is vestigial. Active-subscriber deferral is kept.

## Capabilities

### New Capabilities

None — all changes modify existing capabilities.

### Modified Capabilities

- `background-job-execution`: output capture becomes incremental streaming with rotation; pipeline routing drops the default kill timer (omitted hint = no timer); new `Reaped` status and reap-on-passivation requirement; reconciliation delivers Lost notifications; submit ACK carries log path; session state surfaces and prunes reaped entries.
- `tool-call-metadata`: `_timeout_seconds` semantics for background-routed calls — omitted hint means no kill timer (foreground clamping behavior unchanged).
- `session-resume`: idle passivation proceeds with pending tool approvals outstanding (deferral removed); approval responses continue to rehydrate and resume the original turn per existing requirements.

## Impact

- **Code**: `BackgroundJobExecutionActor` (streaming pumps, rotation, per-line redaction), `BackgroundJobManagerActor` (kill-for-session handler, Lost notification, tail read), `BackgroundJobProtocol` (`Reaped` status, kill/ack messages), `ActiveJobInfo` (+ protobuf serializer), `SessionToolExecutionPipeline` (timeout default, ACK text), `LlmSessionActor` (passivation handshake, deferral removal, reaped-entry pruning), `SessionMessageAssembler` (context block).
- **Identity/skills/docs**: `AGENTS.md` template § Background Jobs, `netclaw-operations` SKILL.md (version bump), `docs/runbooks/background-jobs.md` rewrite. Eval suite run required (identity + skill content change) plus one new regression case.
- **Security**: redaction moves from whole-output-at-exit to per-line-at-write; same approval gates, trust context, and concurrency limits apply unchanged. Reap-on-passivation reduces orphaned-process exposure on self-hosted machines.
- **Operations**: no new config knobs; no schema changes; job definition JSON gains no new required fields (`Reaped` is a new status value). Runbook documents the new lifecycle.
- **PRD traceability**: PRD-001 (Netclaw MVP — daemon tool execution surface); no PRD scope change, this corrects defects in the existing background-execution capability.

## Out of Scope (MVP)

- Readiness detection (`_ready_pattern` regex matching) — the agent polls the log or probes the port itself.
- Streaming job output as push notifications to the session — monitoring stays pull-based (`check_background_job`, `file_read`, `grep`).
- Jobs that intentionally survive passivation (detached daemons) — use scheduled tasks instead.
- Changing the 5-job concurrency limit or queueing policy.
