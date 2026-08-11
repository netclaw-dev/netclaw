## 1. Alert contract

- [ ] 1.1 Add `ReminderScheduleFailed` to the `AlertType` enum in
  `src/Netclaw.Configuration/OperationalAlert.cs`.
- [ ] 1.2 Audit every consumer that switches on `AlertType` (doctor, health
  surface, alert render, any severity/label mapping). Confirm each handles
  `ReminderScheduleFailed` with no silent default drop. Fix any that do.
- [ ] 1.3 Confirm the new enum value needs no config-schema change (alerts are
  runtime signals, not `*Config`), and no proto/serialization break for persisted
  or cross-boundary alerts.

## 2. Scheduling-failure surfacing path

- [ ] 2.1 Add `ReportScheduleFailure` to `ReminderManagerActor`, a sibling of
  `ReportExecutionFailure` (`ReminderManagerActor.cs:948`). It reads the current
  definition, increments the persisted `ConsecutiveFailures`, disables at
  `FailurePauseThreshold`, and persists via `_definitionStore.Save`.
- [ ] 2.2 Emit `OperationalAlert.ReminderScheduleFailed` (Warning) on every
  scheduling failure; emit `ReminderAutoDisabled` (Critical) and call
  `PostFailureNoticeToChannel` when the count reaches the threshold. Reuse the
  `_notificationSink` and helpers the execution path uses.
- [ ] 2.3 On disable, cancel any lingering schedule via `CancelScheduleOnlyAsync`,
  matching the execution-failure disable path.
- [ ] 2.4 Do NOT add a silent UTC fallback and do NOT add a config knob.

## 3. Wire the unattended reschedule sites

- [ ] 3.1 Post-fire reschedule (`ReminderManagerActor.cs:582`): on
  `!scheduleResult.IsSuccess`, call `ReportScheduleFailure`; keep executing the
  current occurrence.
- [ ] 3.2 Reconcile restore loop (`ReminderManagerActor.cs:1062`): on failure,
  call `ReportScheduleFailure` instead of silently skipping; still continue the
  loop so one bad reminder does not abort reconcile.
- [ ] 3.3 On a successful (re)schedule at both sites, reset `ConsecutiveFailures`
  to zero (mirror the execution-success reset at `:822`).
- [ ] 3.4 Leave the create/update path (`:290`, `:471`) unchanged — it already
  returns the error synchronously to the `set_reminder` caller.

## 4. Tests

- [ ] 4.1 Actor-level tests in `src/Netclaw.Actors.Tests/Reminders/` with a fake
  notification sink; use `AwaitAssertAsync`, no `Thread.Sleep`/`Task.Delay`.
- [ ] 4.2 Deterministic failure injection: a no-future-occurrence cron
  (`"0 0 30 2 *"`) and an unknown-zone cron (`"CRON_TZ=Not/AZone 0 9 * * *"`).
- [ ] 4.3 Assert: a scheduling failure emits `ReminderScheduleFailed` and
  increments `ConsecutiveFailures`.
- [ ] 4.4 Assert: reaching `FailurePauseThreshold` via scheduling failures
  disables the reminder, emits `ReminderAutoDisabled`, and posts a channel notice.
- [ ] 4.5 Assert: a successful reschedule resets `ConsecutiveFailures` to zero.
- [ ] 4.6 Assert: the post-fire path surfaces the failure AND still runs the
  current occurrence.
- [ ] 4.7 Assert: the reconcile path surfaces the failure and does not silently
  skip the reminder.
- [ ] 4.8 Assert (anti-pattern guard): an unresolvable zone schedules no
  occurrence and is never evaluated in UTC.
- [ ] 4.9 Assert (cross-boundary): the emitted `ReminderScheduleFailed` alert
  reaches its consumer and is surfaced, not dropped by a default branch.

## 5. Operator guidance

- [ ] 5.1 Update the `netclaw-operations` skill
  (`feeds/skills/.system/files/netclaw-operations/`): document that a scheduling
  failure raises an alert and can auto-disable a reminder, and how to read it.
- [ ] 5.2 Bump `metadata.version` in the skill's YAML frontmatter.

## 6. Quality gates

- [ ] 6.1 `dotnet build` clean; full Reminders test suite green.
- [ ] 6.2 `dotnet slopwatch analyze` — no new violations.
- [ ] 6.3 `./scripts/Add-FileHeaders.ps1 -Verify` — headers present on new files.
- [ ] 6.4 Run `./evals/run-evals.sh` because the skill changed (task 5);
  confirm the scheduling/diagnostics cases still pass.
- [ ] 6.5 Commit to the feature branch. No AI/session links in the commit
  message. No push, no PR.
