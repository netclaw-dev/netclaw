# netclaw-scheduling Specification

## Purpose

Define chat-driven scheduled task creation, persistence, isolated execution
via Akka timers, result reporting, task management, and failure handling
guardrails. This capability enables Netclaw to manage its own schedule
through conversation and execute tasks autonomously.

## Requirements

### Requirement: Chat-driven task creation

The agent SHALL create scheduled tasks when the user requests recurring or
timed actions through conversation. The agent SHALL assign a human-readable
task ID and confirm the schedule. Tasks SHALL support fixed interval and cron
expression schedule types. Tasks requesting tool grants that cannot be
satisfied by ACL policy SHALL be rejected at creation time.

#### Scenario: Create interval-based scheduled task

- **GIVEN** the user asks the agent to perform an action on a recurring basis
- **WHEN** the agent parses the request as a fixed-interval schedule
- **THEN** the agent creates a task with the specified interval
- **AND** assigns a human-readable task ID
- **AND** confirms the schedule, next run time, and required tool grants

#### Scenario: Create cron-based scheduled task

- **GIVEN** the user specifies a cron expression for scheduling
- **WHEN** the agent validates the cron expression
- **THEN** the agent creates a task with the cron schedule
- **AND** confirms the resolved next execution time

#### Scenario: Reject task with ungrantable tools

- **GIVEN** the user requests a scheduled task that requires the `shell` tool
- **WHEN** the `shell` grant is not available in the ACL policy for that sender
- **THEN** the agent rejects the task at creation time
- **AND** explains which tool grants are missing

#### Scenario: Task ID collision avoided

- **GIVEN** a task with ID `ebay-check` already exists
- **WHEN** the user requests a new task that would generate the same ID
- **THEN** the agent generates a unique variant of the ID
- **AND** confirms the actual task ID assigned

### Requirement: Schedule persistence

Scheduled tasks SHALL be persisted to disk at
`~/.netclaw/schedules/tasks.json` and SHALL survive process restarts. On
startup, the system SHALL load persisted tasks and re-establish Akka timers
for all active tasks.

#### Scenario: Tasks survive process restart

- **GIVEN** active scheduled tasks exist in `tasks.json`
- **WHEN** the Netclaw process restarts
- **THEN** all persisted tasks are loaded from disk
- **AND** Akka timers are re-established for active tasks
- **AND** paused tasks remain paused

#### Scenario: New task persisted immediately

- **GIVEN** the user creates a new scheduled task through conversation
- **WHEN** the task is confirmed
- **THEN** the task is written to `tasks.json` before the confirmation is sent

#### Scenario: Corrupted tasks file handled gracefully

- **GIVEN** `tasks.json` contains invalid JSON
- **WHEN** the Netclaw process starts
- **THEN** the system logs a warning
- **AND** starts without any scheduled tasks
- **AND** the operator is notified of the corruption

### Requirement: Isolated task execution

Each scheduled task execution SHALL run in a fresh session actor with its own
context. The session SHALL load the agent personality and any relevant project
context overlays. Scheduled sessions SHALL NOT share state with interactive
sessions.

#### Scenario: Fresh session per execution

- **GIVEN** a scheduled task fires
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** agent personality is loaded from soul files

#### Scenario: Scheduled session isolated from interactive sessions

- **GIVEN** an interactive Slack session exists for the same user
- **WHEN** a scheduled task executes
- **THEN** the scheduled session does not read or modify interactive session
  state
- **AND** the interactive session does not see scheduled session turns

#### Scenario: Task tool grants applied to session

- **GIVEN** a scheduled task specifies `tool_grants: ["web_search", "web_fetch"]`
- **WHEN** the task session starts
- **THEN** only the granted tools are available to the session
- **AND** ungrantable tools are not offered to the LLM

### Requirement: Result reporting

Task execution results SHALL be posted to the configured Slack channel. The
system SHALL support a silent-unless-notable mode where routine results are
suppressed and only notable findings are posted.

#### Scenario: Results posted to configured channel

- **GIVEN** a scheduled task has `report_to.channel` configured
- **WHEN** the task execution completes with results
- **THEN** the results are posted to the configured Slack channel

#### Scenario: Silent-unless-notable suppresses routine results

- **GIVEN** a scheduled task is configured with silent-unless-notable mode
- **WHEN** the task execution completes with no notable findings
- **THEN** no message is posted to Slack
- **AND** the execution is logged as completed with no notable output

#### Scenario: Notable results always posted

- **GIVEN** a scheduled task is configured with silent-unless-notable mode
- **WHEN** the task execution produces notable findings
- **THEN** the results are posted to the configured Slack channel
- **AND** the findings are clearly presented

### Requirement: Task management

The agent and CLI SHALL support listing, pausing, resuming, and deleting
scheduled tasks. The agent SHALL provide task status and next-run time
when listing tasks.

#### Scenario: List all scheduled tasks via conversation

- **GIVEN** multiple scheduled tasks exist
- **WHEN** the user asks to see scheduled tasks
- **THEN** the agent lists all tasks with ID, name, status, schedule, and
  next run time

#### Scenario: Pause a scheduled task

- **GIVEN** an active scheduled task exists
- **WHEN** the user asks the agent to pause the task
- **THEN** the task status is set to paused
- **AND** the Akka timer for the task is cancelled
- **AND** the task remains in `tasks.json` with `status: "paused"`

#### Scenario: Resume a paused task

- **GIVEN** a paused scheduled task exists
- **WHEN** the user asks the agent to resume the task
- **THEN** the task status is set to active
- **AND** the Akka timer is re-established
- **AND** the next run time is calculated from the current time

#### Scenario: Delete a scheduled task

- **GIVEN** a scheduled task exists
- **WHEN** the user asks the agent to delete the task
- **THEN** the task is removed from `tasks.json`
- **AND** the Akka timer is cancelled
- **AND** the agent confirms deletion

#### Scenario: Manage tasks via CLI

- **GIVEN** active scheduled tasks exist
- **WHEN** the operator runs CLI commands for schedule management
- **THEN** the CLI supports list, pause, resume, and delete operations
- **AND** changes are reflected in `tasks.json`

### Requirement: Failure handling and guardrails

The system SHALL track consecutive failures per task. After the configured
failure threshold (default: 5), the task SHALL be automatically paused and
the operator notified. The system SHALL enforce a maximum concurrent execution
limit (default: 3) and a per-task execution timeout (default: 5 minutes).

#### Scenario: Consecutive failures auto-pause task

- **GIVEN** a scheduled task has failed 5 consecutive times
- **WHEN** the 5th failure is recorded
- **THEN** the task is automatically paused
- **AND** the operator is notified in the task's reporting channel
- **AND** the notification includes the last failure reason

#### Scenario: Successful execution resets failure counter

- **GIVEN** a scheduled task has 3 consecutive failures recorded
- **WHEN** the next execution succeeds
- **THEN** the consecutive failure counter is reset to zero

#### Scenario: Max concurrent execution limit enforced

- **GIVEN** 3 scheduled tasks are currently executing (at the default limit)
- **WHEN** another scheduled task timer fires
- **THEN** the execution is deferred until a slot becomes available
- **AND** the deferral is logged

#### Scenario: Execution timeout enforced

- **GIVEN** a scheduled task is executing
- **WHEN** the execution exceeds the configured timeout (default: 5 minutes)
- **THEN** the execution is terminated
- **AND** the result is recorded as a timeout failure
- **AND** the failure contributes to the consecutive failure counter

### Requirement: Execution history CLI command

The CLI SHALL provide a `netclaw reminder history <id>` subcommand that
reads and displays the execution history for a given reminder. The command
SHALL accept an optional `--last N` flag (default: 20) to limit the number
of records shown. Output SHALL be formatted as a table with columns:
`fired_at`, `status`, `duration`, `session_id`. If no history file exists
for the given ID, the command SHALL print a clear "no history recorded"
message and exit with code 0.

#### Scenario: History displayed for a reminder with records

- **WHEN** the operator runs `netclaw reminder history daily-summary`
- **THEN** the most recent 20 execution records are shown as a table
- **AND** each row includes fired_at (UTC), success/failure status,
  duration in ms, and the session ID

#### Scenario: Limit applied with --last flag

- **WHEN** the operator runs `netclaw reminder history daily-summary --last 5`
- **THEN** only the 5 most recent records are shown

#### Scenario: No history file returns graceful message

- **WHEN** the operator runs `netclaw reminder history new-reminder`
  and no history file exists for `new-reminder`
- **THEN** the command prints "No execution history recorded for new-reminder"
- **AND** exits with code 0

#### Scenario: Unknown reminder ID returns error

- **WHEN** the operator runs `netclaw reminder history nonexistent-id`
  and no reminder definition exists for that ID
- **THEN** the command exits with a non-zero code and a clear error message

### Requirement: get_reminder_history agent tool

The system SHALL provide a `get_reminder_history` tool requiring the
`scheduling` grant. The tool SHALL accept a `reminder_id` parameter and an
optional `last` parameter (default: 20, max: 100). The tool SHALL return a
structured list of execution records enabling the agent to assess job health
inline. If no history exists, the tool SHALL return an empty list.

#### Scenario: Agent queries recent executions

- **GIVEN** the agent holds the `scheduling` grant
- **WHEN** the agent calls `get_reminder_history` with `reminder_id: "daily-summary"`
- **THEN** the tool returns up to 20 recent execution records
- **AND** each record includes firedAt, success, durationMs, sessionId,
  and errorMessage

#### Scenario: Agent enforces max record count

- **WHEN** the agent calls `get_reminder_history` with `last: 200`
- **THEN** the tool returns at most 100 records

#### Scenario: Tool rejected without scheduling grant

- **GIVEN** the current session does not hold the `scheduling` grant
- **WHEN** the agent attempts to call `get_reminder_history`
- **THEN** the tool call is rejected by the ACL policy
- **AND** the agent receives a permission-denied response
