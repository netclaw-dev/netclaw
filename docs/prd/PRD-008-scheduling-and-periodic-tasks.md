# PRD-008: Scheduling and Periodic Tasks

## Status

- State: Draft for execution (new)
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-002`, `PRD-007`

## Goal

Enable Netclaw to manage its own schedule through conversation. The operator
can say "check this every 6 hours" and Netclaw persists the task, executes it
on schedule, and reports results back to the appropriate Slack channel.

## Product Outcomes

1. Operator can create scheduled tasks through natural conversation.
2. Scheduled tasks survive process restarts.
3. Task results are posted to the originating or configured Slack channel.
4. Operator can manage (list, pause, delete) scheduled tasks via CLI or chat.

## Scheduling Architecture

### Chat-Driven Task Creation

The agent creates scheduled tasks through conversation:

```
User: "Check eBay for RTX 5090 listings every 6 hours and let me know if
      any are under $1500"
Agent: "I'll check eBay for RTX 5090 listings every 6 hours and alert you
       if any are under $1500. Created schedule 'ebay-rtx-5090-check'
       (every 6h). Next run: 2026-02-21T16:00:00Z."
```

### Schedule Types (MVP)

- **Fixed interval** (`every`): run every N minutes/hours/days
- **Cron expression** (`cron`): standard 5-field cron syntax
- **One-shot** (`at`): run once at a specific time (post-MVP)

### Execution Model

Each scheduled task execution:

1. Creates a fresh session actor (isolated from interactive sessions)
2. Loads the task's instruction prompt as the user message
3. Loads the agent's personality + any project context overlays
4. Grants the task's configured tool permissions
5. Executes the LLM turn loop
6. Posts results to the configured Slack channel/thread
7. Logs execution outcome (success, failure, skipped)

### Persistence

Scheduled tasks are persisted to `~/.netclaw/schedules/tasks.json`:

```json
{
  "tasks": [
    {
      "id": "ebay-rtx-5090-check",
      "name": "eBay RTX 5090 price check",
      "created_by": "slack:U0123OWNER",
      "created_at": "2026-02-21T10:00:00Z",
      "schedule": {
        "type": "interval",
        "interval_minutes": 360
      },
      "instruction": "Search eBay for RTX 5090 listings. Report any under $1500.",
      "report_to": {
        "channel": "C0123ALERTS",
        "thread_ts": null
      },
      "tool_grants": ["web_search", "web_fetch"],
      "status": "active",
      "last_run": "2026-02-21T10:00:00Z",
      "last_result": "success",
      "next_run": "2026-02-21T16:00:00Z"
    }
  ]
}
```

### Akka Timer Integration

Scheduled tasks use Akka's built-in timer/scheduler system:

- A `ScheduleManagerActor` loads tasks from disk at startup
- Each active task gets a recurring timer message
- Timer fires dispatch a `RunScheduledTask` command to a scheduling actor
- The scheduling actor creates a fresh session for execution
- Results are broadcast back through pub/sub for Slack delivery

### Guardrails

- **Max concurrent executions**: configurable limit (default: 3)
- **Execution timeout**: per-task timeout (default: 5 minutes)
- **Consecutive failure tracking**: disable after N consecutive failures
  (default: 5), notify operator
- **Cooldown**: minimum interval between runs (prevents accidental tight loops)

## Requirements

### SCHED-001 Chat-Driven Creation

The agent SHALL create scheduled tasks when the user requests recurring or
timed actions through conversation. The agent assigns a human-readable task
ID and confirms the schedule.

### SCHED-002 Schedule Persistence

Scheduled tasks SHALL be persisted to disk (`schedules/tasks.json`) and
survive process restarts. Tasks are loaded at startup and timers are
re-established.

### SCHED-003 Isolated Execution

Each scheduled task execution SHALL run in a fresh session actor with its
own context. Scheduled sessions do not share state with interactive sessions.

### SCHED-004 Result Reporting

Task execution results SHALL be posted to the configured Slack channel. If
execution produces nothing notable, a brief acknowledgment is posted (or
suppressed if configured for silent-unless-notable).

### SCHED-005 Task Management

The agent and CLI SHALL support:
- List all scheduled tasks with status and next-run time
- Pause/resume individual tasks
- Delete tasks
- Show execution history for a task
- Trigger an existing task to run immediately via CLI for testing and debugging

### SCHED-006 Tool Grants

Each scheduled task SHALL specify its required tool grants. These are checked
against ACL policy at execution time. Tasks requesting ungrantable tools fail
at creation time.

### SCHED-007 Failure Handling

Consecutive failures SHALL be tracked. After the configured failure threshold,
the task is automatically paused and the operator is notified in the reporting
channel.

### SCHED-008 Heartbeat System (Post-MVP)

A periodic heartbeat check that reads a `HEARTBEAT.md` checklist and processes
items that need attention. If nothing needs attention, no message is sent.
Deferred to Phase 2.

## Non-Goals (MVP)

- Event triggers (fire on channel message matching regex)
- Webhook triggers (fire on incoming POST)
- One-shot scheduled tasks (fire once at a specific time)
- Per-routine persistent state (JSON blob per task)
- Heartbeat system

## Acceptance Criteria

1. User can create a scheduled task through conversation.
2. Scheduled tasks survive process restart.
3. Timer fires create fresh session actors for execution.
4. Results are posted to the configured Slack channel.
5. CLI can list, pause, and delete scheduled tasks.
6. Tasks with ungrantable tools are rejected at creation.
7. Consecutive failures pause the task and notify the operator.
8. Max concurrent execution limit is enforced.
9. CLI can trigger an enabled scheduled task to run immediately without changing
   its existing schedule.

## Cross-References

- MVP scope: PRD-001 (FR-012)
- Security: PRD-002 (tool grants)
- CLI management: PRD-004
- Provider for execution: PRD-005
- Tool access: PRD-006 (MCP), PRD-007 (local tools)
- Agent personality (loaded during execution): PRD-007
- Input adapters (post-MVP triggers): PRD-009
