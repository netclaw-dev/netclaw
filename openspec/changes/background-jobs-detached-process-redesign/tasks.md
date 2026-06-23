# Tasks: background-jobs-detached-process-redesign

## 1. Incremental output streaming

- [x] 1.1 Replace `DrainToWindowAsync` + write-at-exit in `BackgroundJobExecutionActor` with stdout/stderr pump tasks that append each line to `output.log` as produced, redacted per line via `SecretOutputRedactor`, flushed per line; pumps keep draining on write failure so the child never deadlocks on a full pipe
- [x] 1.2 Implement single-slot rotation: when `output.log` exceeds the threshold (~5 MB), rotate to `output.1.log` (replacing any prior) and continue streaming into a fresh current log
- [x] 1.3 Read the completion output tail back from the on-disk log (seek-from-end) instead of process-lifetime memory; report truncated capture loudly when rotation occurred
- [x] 1.4 Switch `BackgroundJobManagerActor.HandleQuery` tail read from `File.ReadAllText` to bounded seek-from-end
- [x] 1.5 Unit tests: output visible on disk mid-run (`AwaitAssertAsync`, no sleeps), per-line redaction applied, rotation at threshold, completion tail from file, write-failure drain continues

## 2. Timeout default removal

- [x] 2.1 Background routing call sites in `SessionToolExecutionPipeline` pass `meta.TimeoutHintSeconds ?? 0` (no kill timer) instead of the synchronous default
- [x] 2.2 Submit ACK tool-result text includes the output log path alongside the job ID
- [x] 2.3 Extend `BackgroundRoutingTests`: omitted hint → `StartBackgroundJob.TimeoutSeconds == 0`; explicit positive hint honored unchanged; ACK contains log path

## 3. Reap on session passivation

- [x] 3.1 Protocol: add `BackgroundJobStatus.Reaped`, `KillJobsForSession` command + ack message to `BackgroundJobProtocol`
- [x] 3.2 `BackgroundJobManagerActor`: handle `KillJobsForSession` — kill process trees of owned running/pending jobs idempotently, mark definitions `Reaped`, suppress completion delivery for reaped jobs, ack
- [x] 3.3 `ActiveJobInfo`: add reaped marker; extend protobuf snapshot serializer with optional field (wire-compatible); round-trip serialization test
- [x] 3.4 `LlmSessionActor` Passivating: send `KillJobsForSession`, await ack with short timeout before final snapshot; on timeout log loudly and proceed; mark owned `ActiveJobInfo` entries reaped in state before snapshot
- [x] 3.5 Surface reaped entries (status + log path) in the active-jobs context block on rehydration via `SessionMessageAssembler`; prune reaped entries after the next completed turn
- [x] 3.6 Tests: reap handshake ordering (kill before snapshot), ack-timeout proceeds, reaped entry surfaced once then pruned, completion-vs-reap race deduped by job ID

## 4. Lost notification on reconcile

- [x] 4.1 `BackgroundJobManagerActor.HandleReconcile`: deliver standard termination notification (status `Lost`, output log path) to each owning session via `DeliverTrustedSessionTurn`
- [x] 4.2 Tests: reconcile delivers Lost notification with log path; delivery carries persisted originating audience/boundary; dedup by job ID holds on redelivery

## 5. Passivation deferral removal

- [x] 5.1 Remove the `_pendingToolInteractions.Count > 0` early return from the `Ready` receive-timeout handler; keep subscriber deferral and resolved-approval abandonment
- [x] 5.2 Re-audit and update the "passivating session always has empty `_pendingToolInteractions`" invariant (comment near recovery path) and any logic depending on it
- [x] 5.3 Tests: session passivates with pending approval outstanding; approval response after passivation rehydrates and resumes the parked batch (extend existing restart-mid-approval coverage)

## 6. Docs, skills, and evals

- [x] 6.1 Rewrite `docs/runbooks/background-jobs.md`: detached-process lifecycle, streaming log, no default timer, reap-on-passivation, Lost notification
- [x] 6.2 Update `src/Netclaw.Configuration/Resources/AGENTS.md` § Background Jobs: jobs may run indefinitely, log streams live, killed when conversation goes idle, notified on all termination including Lost; document alternatives for long detached work (check-back reminders, scheduled tasks)
- [x] 6.3 Update `feeds/skills/.system/files/netclaw-operations/SKILL.md` § Background Jobs with the same semantics + monitoring guidance (poll log for readiness, cancel when done); bump `metadata.version`
- [x] 6.4 Add eval regression case: background job submitted → read live log → check status → cancel → process tree gone
- [x] 6.5 Run `./evals/run-evals.sh` (identity template + skill content changed)

## 7. Quality gates

- [x] 7.1 `dotnet slopwatch analyze` — no new violations
- [x] 7.2 `./scripts/Add-FileHeaders.ps1 -Verify` — headers on all new/changed `.cs` files
- [x] 7.3 Full test suite green
