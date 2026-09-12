## 1. Alert contract

- [x] 1.1 Add `ReminderScheduleFailed` to the `AlertType` enum in
  `src/Netclaw.Configuration/OperationalAlert.cs`.
- [x] 1.2 Audit every consumer that switches on `AlertType` (doctor, health
  surface, alert render, any severity/label mapping). Confirm each handles
  `ReminderScheduleFailed` with no silent default drop. Fix any that do.
- [x] 1.3 Confirm the new enum value needs no config-schema change (alerts are
  runtime signals, not `*Config`), and no proto/serialization break for persisted
  or cross-boundary alerts.

## 2. Scheduling-failure surfacing path

- [x] 2.1 Add `ReportScheduleFailureAsync` to `ReminderManagerActor`, a sibling of
  `ReportExecutionFailure`. It reads the current definition, increments the
  persisted `ConsecutiveFailures`, disables at `FailurePauseThreshold`, and
  persists via `_definitionStore.Save`.
- [x] 2.2 Emit `OperationalAlert.ReminderScheduleFailed` (Warning) on every
  scheduling failure; emit `ReminderAutoDisabled` (Critical) and call
  `PostFailureNoticeToChannel` when the count reaches the threshold. Reuse the
  `_notificationSink` and helpers the execution path uses.
- [x] 2.3 On disable, cancel any lingering schedule via `CancelScheduleOnlyAsync`,
  matching the execution-failure disable path.
- [x] 2.4 Do NOT add a silent UTC fallback and do NOT add a config knob.

## 3. Wire the unattended reschedule sites

- [x] 3.1 Post-fire reschedule: on `!scheduleResult.IsSuccess`, call
  `ReportScheduleFailureAsync`; keep executing the current occurrence.
- [x] 3.2 Reconcile restore loop: on failure, call `ReportScheduleFailureAsync`
  instead of silently skipping; still continue the loop.
- [x] 3.3 Do NOT reset `ConsecutiveFailures` on a successful reschedule. The
  post-fire cron reschedule runs before that occurrence executes, so a reset there
  would erase pending execution-failure accumulation. Recovery is the existing
  reset on successful execution, which proves the reminder scheduled AND ran.
- [x] 3.4 Leave the create/update path unchanged — it already returns the error
  synchronously to the `set_reminder` caller.

## 4. Tests

- [x] 4.1 Actor-level tests in `src/Netclaw.Actors.Tests/Reminders/` with the
  existing fake `TestNotificationSink`; deterministic Ask/assert, no
  `Thread.Sleep`/`Task.Delay`.
- [x] 4.2 Deterministic failure injection: a no-future-occurrence cron
  (`"0 0 30 2 *"`).
- [x] 4.3 Assert: a scheduling failure emits `ReminderScheduleFailed` and
  increments `ConsecutiveFailures`.
- [x] 4.4 Assert: reaching `FailurePauseThreshold` via scheduling failures
  disables the reminder, sets `Failed`, and emits Critical `ReminderAutoDisabled`.
  (Channel-notice assertion deferred — reuses the already-tested
  `PostFailureNoticeToChannel`; test harness wires `NullReminderChannelNotifier`.)
- [ ] 4.5 Recovery reset (parked): after scheduling failures, a successful
  execution resets `ConsecutiveFailures`. Guaranteed by construction — no reset
  code was added at the reschedule sites, and the existing execution-success reset
  is unchanged. A dedicated test needs a fire-simulation harness (see Notes).
- [ ] 4.6 Post-fire "still executes" (parked): the post-fire path surfaces the
  failure AND still runs the current occurrence. Not unit-reproducible without a
  fire that then fails to reschedule (needs environment drift). Covered by the
  shared method plus the existing fire/execution tests (see Notes).
- [x] 4.7 Assert: the reconcile path surfaces the failure and does not silently
  skip the reminder.
- [x] 4.8 Assert (anti-pattern guard): a reminder that cannot compute an
  occurrence installs no timer (`NextFire` is null) — no silent fallback.
- [x] 4.9 Cross-boundary: satisfied by audit (no exhaustive `AlertType` switch or
  map exists; consumers render generically off `Type`/`Summary`/`Severity`) plus
  the emission assertion in 4.3.

## 5. Operator guidance

- [x] 5.1 Update the `netclaw-operations` skill scheduling reference: document that
  a scheduling failure raises `ReminderScheduleFailed`, counts toward the
  threshold, and never falls back to a different time.
- [x] 5.2 Bump `metadata.version` in the skill's YAML frontmatter (2.46.0 → 2.47.0).

## 6. Quality gates

- [x] 6.1 `dotnet build` clean; full Reminders test suite green (136/136).
- [x] 6.2 `dotnet slopwatch analyze` — no new violations from this change. One
  pre-existing SW004 warning in `PowerShellHostProbeTests.cs` (outside this diff).
- [x] 6.3 `./scripts/Add-FileHeaders.ps1 -Verify` — all files have headers.
- [ ] 6.4 BLOCKED — `./evals/run-evals.sh` requires eval-target credentials
  (`NETCLAW_EVAL_PROVIDER_TYPE/ENDPOINT/MODEL_ID`), prompts interactively when
  unset, and builds a Docker image that makes real LLM calls. Needs the operator's
  environment. Skill change is additive (a scheduling-failure paragraph); the
  scheduling/diagnostics eval cases assert skill activation and knowledge, so
  regression risk is low, but the gate must be run by the operator.
- [x] 6.5 Commit implementation to the feature branch. No AI/session links in the
  commit message. No push, no PR.

## Notes (parked / for review)

- **Health message shape (design fork, D7):** scheduling failures surface via the
  existing `ReminderHealthResponse.FailedCount` (verified by test). The richer
  "enabled-but-unscheduled" count is NOT built — it needs an async `HandleGetHealth`
  and a new message field. Parked for Aaron.
- **4.5 / 4.6 fire-simulation gap:** the post-fire reschedule-failure path is
  wired and calls the same `ReportScheduleFailureAsync` the reconcile tests
  exercise fully. A dedicated post-fire test needs a reminder that fires then
  fails to reschedule, which only happens under environment drift (tz/tzdata) and
  is not reproducible without mocking `TimeZoneInfo` static calls. Left uncovered
  by unit test on purpose rather than adding a brittle harness.
- **Skill version collision:** bumped 2.46.0 → 2.47.0. If PR #1789 (CRON_TZ) also
  bumps the same file, resolve the version line at merge.
