## 1. Planning Alignment

- [x] 1.1 Update `docs/prd/PRD-008-scheduling-and-periodic-tasks.md` so manual CLI execution is no longer an MVP non-goal and is included in task-management acceptance criteria.
- [x] 1.2 Run `openspec validate manual-reminder-execution --type change` and resolve spec-artifact errors before coding.

## 2. Actor Runtime

- [x] 2.1 Add reminder execution source tracking (`scheduled` / `manual`) to the reminder actor protocol, internal completion message, and history record shape with legacy history defaulting to `scheduled`.
- [x] 2.2 Add `RunReminderNowCommand` / response handling in `ReminderManagerActor` with gates for missing authorization, scheduling disabled, missing reminder, disabled reminder, expired recurring reminder, same-reminder in-flight, and global concurrency full.
- [x] 2.3 Start manual runs through `ReminderExecutionActor` with no Akka.Reminders envelope, no schedule mutation, no cron reschedule, and no one-shot consumption.
- [x] 2.4 Ensure manual completion appends history but does not update scheduled consecutive-failure counters or scheduled auto-disable state.

## 3. API and CLI

- [x] 3.1 Add `POST /api/reminders/{id}/run` under the existing reminder route group and restrict it to Operator authority.
- [x] 3.2 Add `DaemonApi.RunReminderAsync` and `netclaw reminder run <id>` with clear success, daemon-offline, not-found, disabled, busy, and scheduling-disabled messages.
- [x] 3.3 Update reminder CLI help text and operator-facing output to mention immediate execution.

## 4. Tests

- [x] 4.1 Add actor tests proving manual run success dispatches, missing-authorization/disabled/missing/expired/busy gates reject without dispatch/history, global concurrency rejects without queueing, and one-shot manual completion leaves the reminder enabled.
- [x] 4.2 Add actor tests proving manual success/failure leaves scheduled failure accounting unchanged and `CurrentSession` manual execution uses no scheduler ack/redelivery.
- [x] 4.3 Add history-store tests for `source` persistence and legacy records without `source` reading as `scheduled`.
- [x] 4.4 Add daemon endpoint tests for Operator authorization and command/response mapping.
- [x] 4.5 Add CLI tests for `netclaw reminder run <id>` success and daemon rejection output.

## 5. Docs, Skills, and Verification

- [x] 5.1 Update `feeds/skills/.system/files/netclaw-operations/references/scheduling.md` and bump `netclaw-operations` `metadata.version`.
- [x] 5.2 Run targeted tests for `Netclaw.Actors.Tests`, `Netclaw.Daemon.Tests`, and `Netclaw.Cli.Tests` covering the changed surfaces.
- [ ] 5.3 Run repository quality gates: `dotnet test`, `dotnet slopwatch analyze`, `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`, and `git diff --check`.
  - Blocked: full `dotnet test --no-restore` exhausts local disk (`No space left on device` / filesystem 100%). Completed `dotnet slopwatch analyze`, file-header verification, and `git diff --check`.
- [x] 5.4 Run `./evals/run-evals.sh` because system skill guidance changed, or document an environmental blocker with evidence.
  - Blocked: eval target credentials are not configured in this environment (`NETCLAW_EVAL_PROVIDER_TYPE`, `NETCLAW_EVAL_PROVIDER_ENDPOINT`, `NETCLAW_EVAL_MODEL_ID`).
- [x] 5.5 Re-run `openspec validate manual-reminder-execution --type change` after implementation and keep artifacts aligned with code.
