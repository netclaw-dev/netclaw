## Why

Netclaw surfaces reminder *execution* failures loudly: it tracks consecutive
failures, auto-pauses a reminder at a threshold, emits an operational alert, and
posts a channel notice. Reminder *scheduling* failures get none of this. When
`ScheduleDefinitionAsync` cannot compute the next fire — an unresolvable
`CRON_TZ` zone after tzdata or host drift, a cron with no future occurrence, or
an uninitialized client — the caller only writes a log line and moves on. The
reminder stays `Enabled`, raises no alert, bumps no counter, and never fires.

This is a silent failure. It violates the constitution's "No silent fallbacks —
fail loudly" rule, and it leaves SCHED-007 (PRD-008) only half-implemented:
SCHED-007 requires consecutive-failure tracking and operator notification for
reminder failures, not only for the execution phase.

## What Changes

- Add a `ReportScheduleFailure` path in `ReminderManagerActor`, a sibling of the
  existing `ReportExecutionFailure`. It reuses the same seam: bump the persisted
  `ConsecutiveFailures` count, auto-disable at the existing
  `FailurePauseThreshold`, emit an `OperationalAlert`, and post a channel notice.
- Add one alert type: `AlertType.ReminderScheduleFailed` (Warning). Reuse the
  existing `ReminderAutoDisabled` (Critical) when a scheduling failure crosses
  the threshold.
- Route both reschedule sites through the new path: the post-fire reschedule and
  the startup reconcile restore loop. Today both drop the failure.
- A successful (re)schedule resets `ConsecutiveFailures` to zero, so a transient
  scheduling failure does not accumulate forever.
- Extend the reminder health count so it reports enabled reminders that have no
  active schedule, not only reminders with a non-zero failure count.
- Update the `netclaw-operations` skill: document that a scheduling failure
  raises an alert and can auto-disable a reminder, and how to read it.
- Reject any silent fallback to UTC when a zone does not resolve. A wrong-time
  fire is worse than a missed fire. Scheduling failure fails loud.

No config knob. No schema, proto, or storage change. Not breaking.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `netclaw-scheduling`: the "Failure handling and guardrails" requirement extends
  from execution failures to scheduling failures. Consecutive-failure tracking,
  auto-pause at `FailurePauseThreshold`, alert emission, and channel notice apply
  when a reminder cannot compute its next fire, at both the post-fire reschedule
  and the startup reconcile. A successful (re)schedule resets the count. The
  health/status count reports enabled-but-unscheduled reminders.

## Impact

- **Code:** `src/Netclaw.Actors/Reminders/ReminderManagerActor.cs` (new
  `ReportScheduleFailure`; call it from the post-fire reschedule and the
  reconcile restore loop; extend the health count).
- **Alert contract:** `src/Netclaw.Configuration/OperationalAlert.cs` — new
  `AlertType.ReminderScheduleFailed`. Cross-boundary: every consumer of
  `AlertType` (doctor, health surface, alert render) SHALL handle the new value
  with no silent default drop.
- **Skill:** `feeds/skills/.system/files/netclaw-operations/SKILL.md` (or a
  reference file), with a `metadata.version` bump.
- **Tests:** `src/Netclaw.Actors.Tests/Reminders/` — actor-level coverage with a
  fake notification sink.
- **No change:** persistence records, protobuf, config schema, tool schemas,
  `set_reminder` behavior, or default-deny posture.
- **Traceability:** PRD-008 SCHED-007 (failure handling and operator notice),
  SCHED-005 (list/status visibility), SCHED-002 (restart reconcile).
- **Out of scope:** Tiers 2–4 of the hardening plan — reconcile-time quarantine
  of unresolvable schedules, tzdata as an explicit runtime dependency plus an
  `InvariantTimezone` guard and startup self-check, and a hermetic bundled TZDB.
