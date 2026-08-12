## Context

`ReminderManagerActor` is the single actor that owns reminder scheduling. It runs
message-by-message, so all state changes described here are serial — there is no
in-actor concurrency to guard.

Execution failures are already loud. `SettleFailedExecutionAsync`
(`ReminderManagerActor.cs:863`) bumps the persisted `ConsecutiveFailures` count,
auto-disables the reminder at `FailurePauseThreshold`, and calls
`ReportExecutionFailure` (`:948`), which emits an `OperationalAlert`
(`ReminderExecutionFailed`, and `ReminderAutoDisabled` when it disables) and
posts a channel notice via `PostFailureNoticeToChannel`.

Scheduling failures are silent. `ScheduleDefinitionAsync` (`:1174`) catches every
error and returns `ScheduleAttempt.Fail` (`:1240`). The two callers that run with
no human present drop the failure:

- Post-fire reschedule (`:582`) logs a warning and continues.
- Reconcile restore loop (`:1062`) only counts successes; failures are skipped.

The reminder stays `Enabled`, never fires, and raises no alert. This design routes
those two paths through the same surfacing seam execution failures already use.

The create/update path also calls `ScheduleDefinitionAsync` (`:290`, `:471`), but
that path returns the error synchronously to the caller of `set_reminder`, so it
is already loud. This design does not touch it — that avoids a double alert for a
user-initiated action.

## Goals / Non-Goals

**Goals:**

- A scheduling failure at an unattended reschedule site emits an operational
  alert and increments the reminder's consecutive-failure count.
- A scheduling failure that crosses `FailurePauseThreshold` auto-disables the
  reminder, emits the `ReminderAutoDisabled` critical alert, and posts a channel
  notice — identical to the execution-failure outcome.
- A successful (re)schedule resets the consecutive-failure count to zero.
- Scheduling failures are visible in the reminder health signal.
- A single bad startup does not mass-disable reminders.

**Non-Goals:**

- No new failure counter, no new threshold, no operator config knob.
- No change to persistence records, protobuf, config schema, tool schemas, or
  `set_reminder` behavior.
- No silent fallback to UTC for an unresolvable zone.
- No reconcile-time quarantine, no tzdata-as-explicit-dependency work, no bundled
  TZDB. Those are Tiers 2–4, out of scope here.

## Decisions

### D1: Reuse the execution-failure seam, do not build a parallel one

Add `ReportScheduleFailure`, a sibling of `ReportExecutionFailure`, that shares
the same machinery: the persisted `ConsecutiveFailures` field, the
`FailurePauseThreshold` constant, `_notificationSink`, `OperationalAlert`, and
`PostFailureNoticeToChannel`.

Rationale: the constitution's "reuse before you add" rule. A parallel counter or
threshold would duplicate state, drift from the execution path, and add config
surface. _Alternative rejected:_ a separate `_scheduleFailureCounts` with its own
threshold — more state, two operator signals for one condition ("the reminder is
broken"), and config-schema churn.

### D2: One shared consecutive-failure count for both failure kinds

A scheduling failure and an execution failure both increment the same
`ConsecutiveFailures` field on the reminder definition. Either kind of success
resets it to zero.

Rationale: from the operator's view the reminder is either working or not. A
reminder that cannot schedule is as broken as one that cannot execute. One count
gives one clear signal and one auto-disable rule. _Alternative rejected:_
separate counts — forces the operator to reason about two numbers and two
thresholds for one failing reminder.

Trade-off: a mix of one execution failure and four scheduling failures disables
the reminder at five total. That is correct — five consecutive failures of any
kind means the reminder does not work.

### D3: Hook only the two unattended reschedule sites

Call `ReportScheduleFailure` from the post-fire reschedule (`:582`) and the
reconcile restore loop (`:1062`). Do not touch the create/update path — it already
returns the error to the user synchronously.

Rationale: alert only where no human sees the failure. Alerting on a
user-initiated create failure would duplicate the tool-level error the user
already gets.

### D4: Reconcile cannot mass-disable on one bad startup

The reconcile restore loop increments each failing reminder's count by exactly
one per startup. With `FailurePauseThreshold` at five, one bad startup (for
example, a transient missing-tzdata state) raises a Warning alert per affected
reminder but disables none. Auto-disable needs the failure to persist across
several starts, or to combine with post-fire failures.

Rationale: a transient environmental fault at boot must not nuke every reminder.
The threshold already gives this property for free — no special reconcile logic.
_Alternative rejected:_ suppress reconcile alerts entirely — that reintroduces
the silent failure this change removes. The reconcile summary log (`:1127`) still
records the aggregate count for a fast operator read.

Trade-off: many failing reminders at boot produce many Warning alerts (one each).
That is acceptable — the operator needs to know which reminders are affected. If
alert volume becomes a problem, a later change can aggregate; this change does
not pre-optimize.

### D5: No silent UTC fallback

When a zone does not resolve, the schedule fails and is surfaced. It is never
silently evaluated in UTC.

Rationale: a reminder set for 09:00 Brussels that fires at 09:00 UTC is a silent
wrong-time action — worse than a missed fire, and a direct violation of the "No
silent fallbacks" rule, which calls out correctness escalation. Availability does
not outrank correctness here.

### D6: New alert type, with a mandatory consumer audit

Add `AlertType.ReminderScheduleFailed` (Warning). Reuse `ReminderAutoDisabled`
(Critical) at the threshold.

Cross-boundary rule: every consumer that switches on `AlertType` (doctor, health
surface, alert render) SHALL handle the new value with no silent default drop. An
emitted-but-undisplayed alert is the same silent failure in a new place. The
implementation audits all `AlertType` consumers, and a test asserts the alert
reaches the consumer.

### D7: Health signal — surface via the existing count, do not reshape the message (parked fork)

`HandleGetHealth` (`:1314`) returns `ReminderHealthResponse(enabledCount,
activeExecutions, failingCount)`, where `failingCount = Count(ConsecutiveFailures
> 0)`. Because D2 bumps `ConsecutiveFailures` on a scheduling failure, scheduling
failures appear in `failingCount` for free. This change relies on that and does
NOT alter the health message contract.

A fuller signal — "enabled reminders with no active timer" — would need
`HandleGetHealth` to become async, diff enabled definitions against the live
scheduled set (`ListScheduledRemindersAsync`), and add a field to
`ReminderHealthResponse`. That changes an actor message contract and a
sync handler to async.

**This is a design fork and is parked for review, not decided here.** The core
win (scheduling failures become visible and alertable) does not depend on it. See
Open Questions.

## Risks / Trade-offs

- **Alert storm at boot** → D4 keeps auto-disable off for a single bad startup;
  Warning alerts still fire per reminder so the fault is visible; the reconcile
  summary log carries the aggregate count.
- **New enum value not handled by a consumer** → silent failure moves downstream.
  Mitigation: D6 consumer audit plus a test that asserts the alert is surfaced.
- **Shared counter conflates failure kinds** → accepted by D2; the alert message
  and log name the failure kind, so the operator can still tell them apart.
- **Read-modify-write on the definition** → the actor is single-threaded, so the
  reschedule message and the execution-settle message are processed serially.
  `ReportScheduleFailure` reads the current definition before it mutates, the same
  pattern `SettleFailedExecutionAsync` uses. No lost update.
- **`AlertType` serialization** → adding an enum value must not break a persisted
  or cross-boundary alert representation. Verified during implementation; alerts
  are runtime signals, not config, so no config-schema change is expected.

## Open Questions

1. **Health message shape (D7).** Keep the minimal approach (scheduling failures
   show up in the existing `failingCount`), or add an explicit
   `UnscheduledCount` — accepting an async `HandleGetHealth` and a
   `ReminderHealthResponse` field? Recommendation: ship minimal now; add the
   explicit count only if operators need to distinguish "failing" from
   "unscheduled." Parked for Aaron.
