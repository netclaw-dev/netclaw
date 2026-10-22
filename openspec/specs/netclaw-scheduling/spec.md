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
expression schedule types. Task creation SHALL NOT grant tool authority. Each
tool call of a task SHALL pass tool authorization when the task runs, with the
stored task audience (`tool-authorization` TA-3 and TA-4). Tool grant
categories are metadata; the per-audience tool allow lists are the control.

Reminder definitions minted through conversation, tool calls, CLI, REST, or
import SHALL persist an execution audience that is less than or equal to the
creator's current source audience / authority. For conversational or tool-
created reminders, omitted `audience` SHALL inherit the audience of the
creating channel/session rather than the deployment default. Lowering audience
is always allowed.

#### Scenario: Create interval-based scheduled task

- **GIVEN** the user asks the agent to perform an action on a recurring basis
- **WHEN** the agent parses the request as a fixed-interval schedule
- **THEN** the agent creates a task with the specified interval
- **AND** assigns a human-readable task ID
- **AND** confirms the schedule and next run time

#### Scenario: Create cron-based scheduled task

- **GIVEN** the user specifies a cron expression for scheduling
- **WHEN** the agent validates the cron expression
- **THEN** the agent creates a task with the cron schedule
- **AND** confirms the resolved next execution time

#### Scenario: Reject task with ungrantable tools

- **GIVEN** a Team-audience scheduled task whose prompt asks for `shell_execute`
- **WHEN** the task runs and the model calls `shell_execute`
- **THEN** tool authorization denies the call with `tool_not_allowed_for_audience_profile`
- **AND** the task definition itself carried no tool authority

#### Scenario: Task ID collision avoided

- **GIVEN** a task with ID `ebay-check` already exists
- **WHEN** the user requests a new task that would generate the same ID
- **THEN** the agent generates a unique variant of the ID
- **AND** confirms the actual task ID assigned

#### Scenario: Omitted conversational audience inherits source audience

- **GIVEN** a reminder is created from a Team-audience Slack session
- **AND** the request omits `audience`
- **WHEN** the reminder is persisted
- **THEN** the stored reminder audience is `Team`
- **AND** execution does not fall back to the deployment default later

#### Scenario: Lower audience override allowed

- **GIVEN** a reminder is created from a Personal-audience session
- **WHEN** the creator explicitly sets `audience` to `Team`
- **THEN** the reminder is accepted
- **AND** the stored reminder audience is `Team`

#### Scenario: Broader audience override rejected

- **GIVEN** a reminder is created from a Team-audience session
- **WHEN** the creator explicitly sets `audience` to `Personal`
- **THEN** the reminder is rejected before persistence
- **AND** the error explains that the requested audience exceeds the creator's current authority

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

Each scheduled task execution SHALL run in a mode selected explicitly at
set time by `ReminderDefinition.Delivery.Kind`:

- `DeliveryKind.CurrentSession` → **re-enter the originating session**
  (no new session actor created; rehydrates from Akka.Persistence if
  passivated).
- `DeliveryKind.Channel` → **spawn a fresh isolated session** whose LLM
  uses the transport's canonical notification tool (e.g.,
  `send_slack_message` for `Transport = "slack"`) to post to
  `Delivery.Address`.
- `DeliveryKind.None` → **spawn a fresh isolated session** that runs
  the task and records history but emits no external output.

Isolated sessions (Channel / None) SHALL load the agent personality and
project context overlays and SHALL NOT share state with interactive
sessions. Session-reentry executions (CurrentSession) SHALL reuse the
persisted state of the originating session actor and SHALL NOT create a
new session.

Execution mode SHALL NOT be inferred from the presence or absence of
any other field — only `Delivery.Kind` determines the branch.

Execution MAY trust the stored reminder audience because reminder
minting and import paths SHALL validate the persisted audience before
the definition is saved. In all modes, the effective audience at
execution time SHALL be the stored reminder audience, not the live
audience of the originating session.

#### Scenario: Fresh session for Channel delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = Channel`,
  `Delivery.Transport = "slack"`, and `Delivery.Address = "C0123ABC"`
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** agent personality is loaded from soul files
- **AND** the LLM's available tools include `send_slack_message`

#### Scenario: Fresh session for None delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = None`
- **WHEN** the timer tick triggers execution
- **THEN** a new session actor is created with entity key
  `schedule/{taskId}/{runTs}`
- **AND** the task instruction is delivered as the user message
- **AND** no notification tool (`send_slack_message`, etc.) is present
  in the LLM's tool set
- **AND** `ReminderExecutionCompleted(success=true)` is reported on
  natural turn completion

#### Scenario: Session re-entry for CurrentSession delivery

- **GIVEN** a reminder persisted with `Delivery.Kind = CurrentSession`,
  `Delivery.SessionId` set, and `Delivery.OriginChannelType` set
- **WHEN** the timer tick triggers execution
- **THEN** the existing session actor for the persisted `SessionId` is
  addressed (rehydrating from Akka.Persistence if currently passivated)
- **AND** NO new session actor is created with a `schedule/...` entity key
- **AND** the reminder turn is delivered as a `SendUserMessage` whose
  `MessageSource.ChannelType` matches `Delivery.OriginChannelType`

#### Scenario: Scheduled session isolated from interactive sessions

- **GIVEN** an interactive Slack session exists for the same user
- **WHEN** a Channel-kind scheduled task executes
- **THEN** the scheduled session does not read or modify interactive
  session state
- **AND** the interactive session does not see scheduled session turns

#### Scenario: Task tool grants applied to session

- **GIVEN** a scheduled task specifies `tool_grants: ["web_search", "web_fetch"]`
- **WHEN** the task session starts
- **THEN** only the granted tools are available to the session
- **AND** ungrantable tools are not offered to the LLM

#### Scenario: Execution uses validated stored audience

- **GIVEN** a reminder definition was accepted with stored audience `Public`
- **WHEN** the reminder later executes on schedule
- **THEN** the execution session uses stored audience `Public`
- **AND** it does not recompute audience from the deployment posture default

### Requirement: Result reporting

Task execution results SHALL be delivered according to
`ReminderDefinition.Delivery.Kind`:

- `Channel`: results SHALL be posted to `Delivery.Address` via
  `Delivery.Transport`'s canonical notification tool, called by the
  isolated session's LLM. `Delivery.Address` SHALL always be a
  canonical identifier produced by the transport's
  `IReminderTargetResolver` (never a raw LLM-supplied string).
- `CurrentSession`: the reminder turn SHALL be routed through the
  originating channel's existing session route. The reminder dispatcher
  SHALL select the gateway from the stored `Delivery.OriginChannelType`:
  `ChannelType.Slack` → `SlackGatewayActor`;
  `ChannelType.Discord` → `DiscordGatewayActor`;
  `ChannelType.Mattermost` → `MattermostGatewayActor`;
  `ChannelType.Tui` or `ChannelType.SignalR` → `SignalRGatewayActor`.
  The channel-level inbound ACL SHALL be bypassed because the
  reminder's audience was validated at minting time. Any other
  `OriginChannelType` SHALL be rejected at `set_reminder` time.
- `None`: no external delivery SHALL be performed. Execution history
  SHALL still be recorded.

Optional `ReminderDefinition.DeliveryInstructions` SHALL guide the content
the LLM produces for `Channel` and `CurrentSession` deliveries but
SHALL NOT affect routing.

#### Scenario: Channel results posted via transport notification tool

- **GIVEN** a reminder with `Delivery.Kind = Channel`,
  `Delivery.Transport = "slack"`, `Delivery.Address = "C0123ABC"`
- **WHEN** the task execution completes with results
- **THEN** the LLM calls `send_slack_message` with the address and the
  result content
- **AND** the results are posted to the Slack channel via the
  reminder's isolated execution session

#### Scenario: CurrentSession Slack delivery routes through existing gateway chain

- **GIVEN** a `CurrentSession` reminder created from a Slack thread
  session with `Delivery.OriginChannelType = Slack`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s
  `SlackGatewayActor` with a `DeliverTrustedSessionTurn` carrying the
  originating `SessionId`, reminder prompt, and trusted `MessageSource`
- **AND** the gateway's handler parses the `SessionId` into
  `(channelId, threadTs)` and uses its existing
  `Context.Child(name).GetOrElse(...)` lookup to reach the conversation
  actor
- **AND** `conversation.Forward(msg)` preserves `Sender`
- **AND** `SlackConversationActor`'s handler uses the same lookup
  pattern to reach the thread binding actor
- **AND** `binding.Forward(msg)` preserves `Sender`
- **AND** `SlackThreadBindingActor`'s handler reads `Sender`, builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender` and
  `MessageSource.ReminderId` populated, and offers it to the pipeline
  queue
- **AND** the reminder turn is delivered through the normal
  `ChannelInput` → `ChannelPipeline` → `SendUserMessage` → session
  pipeline
- **AND** the session's streaming response is posted back to the
  original Slack thread via the binding's existing output sink
- **AND** `SlackAclPolicy.EvaluateInbound` is NOT called

#### Scenario: CurrentSession SignalR delivery routes through existing gateway chain

- **GIVEN** a `CurrentSession` reminder created from a SignalR session
  (including TUI) with `Delivery.OriginChannelType` = `Tui` or
  `SignalR` and `Delivery.SessionId = "signalr/{guid}"`
- **WHEN** the reminder fires
- **THEN** the reminder dispatcher `Ask<CommandAck>`s
  `SignalRGatewayActor` with a `DeliverTrustedSessionTurn`
- **AND** `SignalRMessageExtractor.EntityId` matches the message via
  its `IWithSessionId` fallback and extracts the session GUID
- **AND** `GenericChildPerEntityParent` routes the message to the
  existing `SignalRSessionActor` child for that session (creating one
  if needed)
- **AND** `SignalRSessionActor`'s handler reads `Sender`, builds a
  `ChannelInput` with `MessageSource.AckTarget = Sender`, and offers
  it to the pipeline queue
- **AND** if a SignalR client is currently connected, the streaming
  response reaches the client in real time via the existing bridge
- **AND** if no client is currently connected, the session still
  processes the turn and persists `TurnRecorded`; streaming output is
  dropped per the existing `OverflowStrategy.DropHead` behavior and is
  visible on next `ResumeSessionAsync`

#### Scenario: None delivery records history and emits nothing

- **GIVEN** a reminder with `Delivery.Kind = None`
- **WHEN** the task execution completes
- **THEN** no message is posted and no session turn is delivered
- **AND** the execution is recorded in
  `~/.netclaw/reminders/{id}.history.jsonl` with `success=true`

#### Scenario: Mattermost current-session reminder preserves the thread and authority

- **GIVEN** a Team-audience Mattermost session identified by `{channelId}/{rootPostId}`
- **WHEN** `set_reminder` accepts `delivery_kind = current_session`
- **THEN** the stored delivery contains that session ID and `OriginChannelType = Mattermost`
- **AND** the stored audience does not exceed the creator's source audience
- **WHEN** the reminder fires
- **THEN** the dispatcher uses the Mattermost gateway, conversation, and session binding
- **AND** the response reaches the original channel and thread
- **AND** the run succeeds when the session acknowledges the fire, whether or not `delivery_required` is set

#### Scenario: Unsupported current-session origin is rejected before persistence

- **GIVEN** a webhook session without a current-session gateway
- **WHEN** `set_reminder` receives `delivery_kind = current_session`
- **THEN** the tool rejects the reminder before it sends a save command

#### Scenario: A Mattermost post failure does not fail the reminder

- **GIVEN** a Mattermost current-session reminder with `delivery_required = true`
- **AND** the session acknowledged the fire
- **WHEN** the Mattermost post API later rejects the response
- **THEN** the execution still records a successful outcome
- **AND** the reminder system does not retry the fire
- **AND** the session does not retry the post, because a Mattermost or Discord post failure is a `TransportFailure` and the delivery retry covers other failure kinds only
- **AND** the binding reports `DeliveryFailed` with `TransportFailure` to the session
- **AND** the session writes an Error log and raises a `reminder.delivery.failed` alert (see the `netclaw-session` requirement "A failed post on a reminder turn is visible to the operator")

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

The reminder manager SHALL store consecutive failures in each reminder
definition. An execution failure and a scheduling failure SHALL increment the
same count. A successful execution SHALL reset the count. A successful
reschedule alone SHALL NOT reset the count. The post-fire reschedule of a cron
reminder runs before that occurrence executes. A reset at that point erases a
pending execution-failure count.

The manager SHALL disable a reminder when the count reaches
`FailurePauseThreshold`. The disabled definition SHALL remain available for
status and diagnosis.

`FailurePauseThreshold` is not operator-configurable — it lives as an
`internal const` on `ReminderManagerActor`. `Akka.Reminders` applies its
own separate retry budget (`MaxDeliveryAttempts`, library default) to
envelope delivery; Netclaw's threshold is set strictly below the library's
default so the Netclaw-side pause fires first in practice and operators see a
disabled reminder in `netclaw reminders list` before the library would mark an
occurrence terminally failed. If either default changes in a way that breaks
this ordering, add back a single operator knob.

The manager SHALL NOT cap the number of concurrent executions. Capacity was
removed because every execution already has a one-hour absolute limit and
Akka.Reminders owns failure retry, so unbounded scheduling pressure on the LLM
is acceptable.

Each execution SHALL have a one-hour absolute limit. A known timeout SHALL
count as a failed attempt.

A scheduling failure is a failure to compute or install the next occurrence at
an unattended reschedule site. The two unattended sites are the post-fire
reschedule of a recurring reminder and the startup reconcile restore loop.
Causes include an unresolvable `CRON_TZ` time zone, a cron expression with no
future occurrence, and an uninitialized reminder client.

At an unattended site, the manager SHALL do all of the following:

- increment the reminder's `ConsecutiveFailures` count;
- emit an `OperationalAlert.ReminderScheduleFailed` alert at Warning severity;
- disable the reminder when the count reaches `FailurePauseThreshold`, emit an
  `OperationalAlert.ReminderAutoDisabled` alert at Critical severity, and post a
  channel notice.

The manager SHALL NOT evaluate an unresolvable time zone in UTC. The manager
SHALL NOT skip a failed reschedule without a report. A fire at the wrong time is
worse than a missed fire.

The create path and the update path (`set_reminder`) SHALL return a scheduling
error to the caller. Those paths SHALL NOT emit a scheduling-failure alert,
because the caller already sees the error.

#### Scenario: Consecutive failures disable a reminder

- **GIVEN** a reminder has one fewer failure than `FailurePauseThreshold`
- **WHEN** its next execution fails
- **THEN** the manager saves the threshold failure count
- **AND** the manager disables the reminder
- **AND** the definition remains available

#### Scenario: A successful execution resets the failure count

- **GIVEN** a reminder has one or more consecutive failures
- **WHEN** its next execution succeeds
- **THEN** the manager saves a zero failure count

#### Scenario: Reminder fires while other reminders are executing

- **GIVEN** several reminder attempts are active
- **WHEN** another occurrence arrives
- **THEN** the manager starts the new execution immediately
- **AND** no occurrence is skipped or deferred for capacity reasons

#### Scenario: Execution timeout enforced

- **GIVEN** a reminder execution exceeds the per-execution timeout
- **WHEN** the timeout fires
- **THEN** the execution is cancelled and reported as a failure
- **AND** the failure is counted toward `FailurePauseThreshold`

#### Scenario: Post-fire reschedule failure is surfaced

- **GIVEN** a recurring reminder fires
- **AND** the manager cannot compute its next occurrence
- **WHEN** the manager attempts the post-fire reschedule
- **THEN** the manager increments the reminder's `ConsecutiveFailures` count
- **AND** the manager emits an `OperationalAlert.ReminderScheduleFailed` alert
- **AND** the current occurrence still executes

#### Scenario: Reconcile schedule failure is surfaced

- **GIVEN** an enabled reminder whose next occurrence is not computable at startup
- **WHEN** the reconcile restore loop attempts to reschedule it
- **THEN** the manager increments the reminder's `ConsecutiveFailures` count
- **AND** the manager emits an `OperationalAlert.ReminderScheduleFailed` alert
- **AND** the manager does not skip the reminder without a report

#### Scenario: Consecutive scheduling failures disable a reminder

- **GIVEN** a reminder has one fewer scheduling failure than `FailurePauseThreshold`
- **WHEN** the manager reports the next scheduling failure
- **THEN** the manager disables the reminder
- **AND** the manager emits an `OperationalAlert.ReminderAutoDisabled` alert at
  Critical severity
- **AND** the manager posts a channel notice

#### Scenario: A successful execution resets scheduling failures

- **GIVEN** a reminder has two consecutive scheduling failures
- **AND** its schedule recovers, so the reminder fires again
- **WHEN** that occurrence executes successfully
- **THEN** the manager saves a zero failure count

#### Scenario: A successful reschedule alone does not reset the count

- **GIVEN** a cron reminder has a failure count above zero
- **WHEN** the post-fire reschedule of an occurrence succeeds
- **THEN** the manager keeps the current failure count
- **AND** only a later successful execution resets it

#### Scenario: An unresolvable time zone never falls back to UTC

- **GIVEN** a cron reminder with an unresolvable `CRON_TZ` time zone
- **WHEN** the manager attempts a reschedule at an unattended site
- **THEN** the manager schedules no occurrence
- **AND** the manager does not evaluate the reminder in UTC
- **AND** the manager reports the failure through the count and a
  `ReminderScheduleFailed` alert

#### Scenario: One bad startup does not disable many reminders

- **GIVEN** many enabled reminders whose schedules all fail once at startup
- **AND** `FailurePauseThreshold` is greater than one
- **WHEN** the reconcile restore loop runs
- **THEN** the manager increments each affected count by one
- **AND** the manager disables no reminder because of one startup failure
- **AND** the manager emits an `OperationalAlert.ReminderScheduleFailed` alert
  for each affected reminder

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

### Requirement: Envelope-ack-gated delivery ends at admission

The reminder manager SHALL retain each Akka.Reminders envelope until the attempt
has a known outcome. This rule SHALL apply to every delivery kind, so `Channel`
and `None` reminders no longer get an eager acknowledgement.

The execution actor SHALL report its outcome to the manager. It SHALL wait for
`ReminderExecutionAccepted` before it stops.

The manager SHALL acknowledge only a successful execution. For `Channel`
delivery, it SHALL also require all required delivery evidence. A `CurrentSession`
run needs no delivery evidence (see below). It SHALL negatively acknowledge a
known failure.

A `CurrentSession` reminder SHALL use the origin gateway and
`Ask<CommandAck>`. The run SHALL end when the session acknowledges the fire
(admission). The run SHALL NOT wait for a post to the channel, whatever the
value of `DeliveryRequired`. `DeliveryRequired` SHALL have no effect for a
`CurrentSession` reminder. A `CommandNack` or an Ask timeout SHALL fail the run.

The target session SHALL decide alone what happens to an admitted fire. It
SHALL use the stable key of the occurrence, which is built from the occurrence
due time.

#### Scenario: CurrentSession succeeds at admission

- **GIVEN** a `CurrentSession` reminder has `DeliveryRequired = true`
- **WHEN** the target session returns `CommandAck`
- **THEN** the child reports success to the manager
- **AND** the manager acknowledges the occurrence
- **AND** no delivery result is awaited

#### Scenario: CurrentSession with DeliveryRequired=false acks on CommandAck alone

- **GIVEN** a `CurrentSession` reminder with `DeliveryRequired = false` fires
- **WHEN** `CommandAck` is received from the session
- **THEN** the child reports success to the manager

#### Scenario: CurrentSession refusal fails the run

- **GIVEN** a `CurrentSession` reminder fires
- **WHEN** the gateway or the session replies `CommandNack`, or the Ask times out
- **THEN** the child reports a descriptive failure
- **AND** the manager sends a negative acknowledgement

#### Scenario: Channel execution fails

- **GIVEN** a `Channel` reminder starts an isolated execution
- **WHEN** the execution or notification fails
- **THEN** the manager does not acknowledge success
- **AND** the manager sends a negative acknowledgement

#### Scenario: None delivery succeeds

- **GIVEN** a reminder uses `Delivery.Kind = None`
- **WHEN** its execution completes successfully
- **THEN** the manager acknowledges the occurrence

#### Scenario: The child reports success before it stops

- **GIVEN** an execution child reports success
- **WHEN** the manager saves local state and acknowledges the occurrence
- **THEN** the manager sends `ReminderExecutionAccepted`
- **AND** the child stops after that message

#### Scenario: Redelivered CurrentSession reminder is deduped on the target session

- **GIVEN** a `CurrentSession` fire was admitted by the session, so its journal
  holds the input with source key `{reminderId}:{fireTimestampMs}`
- **WHEN** Akka.Reminders redelivers the same envelope after a transient failure
- **THEN** the session acknowledges it without a second input
- **AND** the manager settles the occurrence once, closing the redelivery loop

### Requirement: Reminder delivery guarantees end at admission

The reminder pipeline SHALL provide at-least-once attempt delivery until the
manager confirms execution. For a `CurrentSession` reminder, execution ends at
admission by the session.

A crash before acknowledgement SHALL leave the occurrence eligible for retry. A
crash after acknowledgement SHALL not lose successful work.

The stable occurrence identity and the stored source key of the admitted input
SHALL reduce duplicate work. Netclaw SHALL not claim exactly-once delivery.

#### Scenario: The daemon stops during execution

- **GIVEN** a reminder attempt has not reached manager acknowledgement
- **WHEN** the daemon stops
- **THEN** the acknowledgement lease expires
- **AND** Akka.Reminders can retry the occurrence

#### Scenario: The daemon stops after acknowledgement

- **GIVEN** execution succeeded
- **AND** the manager acknowledged the occurrence
- **WHEN** the daemon stops before one-shot terminal state is saved
- **THEN** durable occurrence status remains `Delivered`
- **AND** reconciliation repairs the one-shot terminal state

#### Scenario: Duplicate across snapshot recovery is caught

- **GIVEN** a `CurrentSession` fire was admitted
- **AND** a `SessionSnapshot` was taken
- **AND** the session later recovers from that snapshot
- **WHEN** Akka.Reminders redelivers the original fire
- **THEN** the snapshot holds the source key of the fire
- **AND** the session acknowledges the redelivery without a second input

#### Scenario: Delivery guarantees documented in reminder-set confirmation

- **GIVEN** a `CurrentSession` reminder is successfully set
- **WHEN** the tool returns its success message
- **THEN** the message conveys that the reminder will fire and deliver a new turn
  to the originating session

### Requirement: Recurring reminder expiration

Recurring reminders (interval and cron) SHALL support an optional `ExpiresAt`
timestamp. When a reminder expires, it SHALL be soft-disabled — the definition
and history are preserved on disk, but no further executions occur.

Backwards compatibility: `ExpiresAt` is stored as a nullable
`ExpiresAtMs` field on `ReminderDefinition`. Existing definitions
without this field deserialize as `null` (no expiration), preserving
current behavior.

#### Scenario: Expired interval reminder auto-disabled on fire

- **GIVEN** an enabled interval reminder with `ExpiresAt` in the past
- **WHEN** Akka.Reminders fires the envelope
- **THEN** the manager disables the reminder without executing it
- **AND** the envelope is acknowledged
- **AND** the definition remains on disk with `Enabled = false`

#### Scenario: Expired cron reminder auto-disabled on fire

- **GIVEN** an enabled cron reminder with `ExpiresAt` in the past
- **WHEN** Akka.Reminders fires the envelope
- **THEN** the manager disables the reminder before rescheduling
- **AND** no new cron schedule is created

#### Scenario: Reconciliation disables expired recurring reminders

- **GIVEN** the daemon restarts
- **AND** one or more recurring reminders have `ExpiresAt` in the past
- **WHEN** reconciliation runs
- **THEN** each expired reminder is disabled and its schedule cancelled
- **AND** the reconciliation result includes the count of disabled-expired
  reminders

#### Scenario: Non-expired recurring reminder fires normally

- **GIVEN** an enabled interval reminder with `ExpiresAt` in the future
- **WHEN** the reminder fires
- **THEN** execution proceeds as normal

#### Scenario: ExpiresIn parameter accepted on set_reminder

- **GIVEN** a user calls `set_reminder` with `schedule_type = "interval"`
  and `expires_in = "24h"`
- **WHEN** the tool processes the request
- **THEN** `ExpiresAt` is computed as `now + 24h` and set on the definition
- **AND** the success response includes the expiration time

#### Scenario: ExpiresIn rejected on one-shot reminders

- **GIVEN** a user calls `set_reminder` with `schedule_type = "once"` and
  `expires_in = "24h"`
- **WHEN** the tool validates the request
- **THEN** an error is returned: "expires_in is not applicable to one-shot
  reminders"

### Requirement: LLM self-cancellation of fulfilled reminders

Recurring reminders SHALL include prompt guidance telling the executing LLM to
call `cancel_reminder` when the reminder's purpose is permanently
fulfilled. This reuses the existing `cancel_reminder` tool
rather than introducing a separate completion tool — fewer tools means
less confusion for smaller models. For a recurring reminder, `cancel_reminder`
SHALL disable the reminder and keep its definition and history.

#### Scenario: LLM self-cancels a fulfilled recurring reminder

- **GIVEN** an enabled interval reminder fires and the LLM executes
- **AND** the LLM determines the task is permanently fulfilled
- **WHEN** the LLM calls `cancel_reminder` with the reminder's ID
- **THEN** the reminder is disabled and its schedule is cancelled
- **AND** the definition and the history stay
- **AND** future fires do not execute

#### Scenario: Prompt guidance includes reminder ID and cancellation instructions

- **GIVEN** a recurring (interval or cron) reminder definition
- **WHEN** the execution actor builds the prompt
- **THEN** the prompt includes guidance to call `cancel_reminder`
- **AND** the guidance includes the reminder's own ID

### Requirement: A reminder has at most one waiting fire in a session

A session SHALL keep at most one waiting fire for each reminder. A fire waits
only while the session's in-memory input queue holds it. A turn that selects a
fire removes it from the queue. When a newer fire of the same reminder arrives
while an earlier fire waits, the session SHALL apply one of two rules:

- While the [child consent pause](../../../docs/spec/GLOSSARY.md#child-consent-pause) applies,
  the newer fire SHALL replace the earlier fire. The session SHALL close the
  earlier fire and store the newer fire in one journal write. The session SHALL
  log `reminder_fire_replaced`. This rule SHALL apply also when a parent
  approval is open at the same time.
- In every other case, the session SHALL acknowledge the newer fire and SHALL
  NOT store it. The session SHALL log `reminder_fire_dropped`. This rule
  applies to a fire that waits only behind a parent approval.

The reminder system SHALL see success for both rules.

These paths put a stored input back in the queue, so the rule holds after a
restart or a passivation for those inputs:

- Recovery rebuilds the queue for inputs that wait behind an open parent
  approval.
- When the child consent pause held the session at recovery, the session
  queues the automated inputs that the pause held (`StartHeldInputs`).
- A restart reminder in `Ready` phase resumes the leading inputs of the first
  requester and queues the other pending inputs. This rule applies also when
  the first input is a slash command. When the slash command keeps the session
  in `Ready` phase, the session starts the next queued input after it closes
  the slash command input.
- A restart reminder in `Processing` or `Compacting` phase queues each pending
  input that no turn selected. It replies `RestartResumeNotNeeded`.

In `Processing` or `Compacting` phase, if one pending input that no turn
selected has a stored authority that does not parse, the restart reminder
replies `CommandNack` and queues no input. In `Ready` phase, the same reply
applies when such an input comes before the first input of another requester,
or is that input.

A stored input that nothing will run is not waiting. A crash, an expired
restart reminder, or a stored authority that does not parse can leave such an
input pending. It SHALL NOT block a later fire. The session SHALL NOT drop or
replace a fire while the earlier fire runs. The session SHALL NOT drop or
replace a fire because another reminder has a waiting fire. A key that is not a
reminder fire key (`{reminderId}:{fireTimeMs}`) SHALL never be dropped or
replaced and SHALL never block a fire.

The decision SHALL cause no side effect. A dropped or duplicate fire SHALL NOT
abandon an open tool batch.

#### Scenario: A newer fire is dropped while the earlier fire waits

- **GIVEN** a reminder fires every 12 minutes
- **AND** the 10:00 fire waits behind a running turn
- **WHEN** the 10:12 fire arrives
- **THEN** the session acknowledges it and stores no input
- **AND** the 10:00 fire runs after the turn
- **AND** the 10:24 fire waits again, and the session admits it

#### Scenario: A fire waits behind a parent approval, also after a restart

- **GIVEN** the 10:00 fire waits behind an open parent approval, and no child consent request is open
- **AND** the daemon restarted, so recovery rebuilt the waiting fire from the journal into the queue
- **WHEN** the 10:12 fire arrives
- **THEN** the session acknowledges it and stores no input

#### Scenario: A newer fire replaces the waiting fire during the child consent pause

- **GIVEN** a child consent request is open, so the child consent pause applies
- **AND** the 10:00 fire waits for the pause
- **WHEN** the 10:12 fire arrives
- **THEN** the session closes the 10:00 fire and stores the 10:12 fire in one journal write
- **AND** the session logs `reminder_fire_replaced`
- **AND** after the pause ends, one turn of the reminder runs, for the 10:12 fire

#### Scenario: A busy restart reminder queues a pending fire

- **GIVEN** a graceful restart interrupted the turn of the 10:00 fire, so the fire stayed pending
- **AND** after the restart, an operator turn runs
- **AND** no child consent request is open
- **WHEN** the restart reminder arrives while the operator turn runs
- **THEN** the session queues the 10:00 fire and replies `RestartResumeNotNeeded`
- **AND** the 10:00 fire runs once, after the operator turn
- **AND** a 10:12 fire that arrives before the 10:00 fire runs is dropped

#### Scenario: A rejected slash command does not strand the inputs behind it

- **GIVEN** a graceful restart left three pending inputs: an unknown slash command of Alice, the 10:00 fire, and a later text input of Alice
- **WHEN** the restart reminder arrives in `Ready` phase
- **THEN** the session closes the slash command input with a skipped turn
- **AND** the 10:00 fire runs once, under the authority of the reminder
- **AND** the text input of Alice runs after the fire, under the authority of Alice
- **AND** no input stays pending

#### Scenario: A fire that a crash left pending does not block the next fire

- **GIVEN** the 10:00 fire was stored and waited behind a running turn
- **AND** the daemon crashed, so the new session holds the 10:00 fire in its journal but not in its queue, and no restart reminder exists
- **WHEN** the 10:12 fire arrives
- **THEN** the session stores it as an input
- **AND** the 10:12 fire runs

#### Scenario: A key that is not a fire key is never dropped

- **GIVEN** a reminder input is stored with the key `weekly:abc` (the time is not digits)
- **WHEN** a reminder input with the key `weekly:def` arrives
- **AND** a reminder input with the key `weekly:1700000000000` arrives
- **THEN** the session stores both as inputs
- **AND** it logs no `reminder_fire_dropped`
- **AND** the stored `weekly:abc` input blocks neither key

#### Scenario: A fire is admitted while the earlier fire runs

- **GIVEN** a running turn was started by the 10:00 fire
- **WHEN** the 10:12 fire arrives
- **THEN** the session stores it as a waiting input
- **AND** both fires run

#### Scenario: Different reminders never drop each other

- **GIVEN** the reminders `check-pr` and `standup` both fire during a busy turn
- **THEN** the session stores both fires
- **AND** a name prefix (`check` and `check-pr`) does not make two reminders equal

#### Scenario: A redelivered fire is a duplicate, not a drop

- **GIVEN** the session admitted the 10:00 fire
- **WHEN** Akka.Reminders redelivers the 10:00 fire
- **THEN** the session acknowledges it and stores no input
- **AND** a recovered session writes no `ToolBatchAbandoned` for it

### Requirement: Reminder delivery target validation

The `set_reminder` tool SHALL validate the structured
`delivery: { kind, transport?, address? }` parameter at invocation
time and SHALL persist only canonical, validated values.

For `delivery.kind = CurrentSession`:

- The tool SHALL require an addressable tool execution context
  (`context.SessionId` present and `context.ChannelType` parseable to
  `ChannelType.Slack`, `ChannelType.Tui`, or `ChannelType.SignalR`).
- The tool SHALL reject any non-null `delivery.transport` or
  `delivery.address` with an actionable error.
- The persisted `ReminderDefinition.Delivery.SessionId` SHALL equal
  `context.SessionId`; `Delivery.OriginChannelType` SHALL equal the
  parsed channel type; `Delivery.Transport` and `Delivery.Address`
  SHALL be null.

For `delivery.kind = Channel`:

- Both `delivery.transport` and `delivery.address` SHALL be non-empty.
- The tool SHALL look up an `IReminderTargetResolver` whose `Transport`
  property equals `delivery.transport` (case-insensitive). If no
  matching resolver is registered, the tool SHALL fail loud with an
  error identifying the unknown transport.
- The matching resolver SHALL be called to canonicalize
  `delivery.address`. The persisted `Delivery.Address` SHALL be the
  resolver's canonical identifier — never the raw LLM input. An
  unresolvable address SHALL cause the tool invocation to fail
  immediately with an actionable error.
- `Delivery.SessionId` and `Delivery.OriginChannelType` SHALL be null.
- Transports without a canonical notification tool
  (SignalR / TUI today) SHALL be rejected for
  `delivery.kind = Channel` with a clear error telling the LLM to use
  `CurrentSession` for those origins.

For `delivery.kind = None`:

- `delivery.transport` and `delivery.address` SHALL both be null.
- All `Delivery.*` fields except `Kind` SHALL be null in the persisted
  definition.
- `DeliveryRequired` SHALL be ignored for `None` deliveries since there
  is no delivery to fail. The field MAY be set to any value; it has no
  effect on execution outcome.

#### Scenario: Hash-prefixed Slack channel resolved to canonical ID

- **GIVEN** a host with a registered `IReminderTargetResolver` whose
  `Transport = "slack"` and that maps `#general` to `C0123ABC`
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"`, `delivery.transport = "slack"`,
  `delivery.address = "#general"`
- **THEN** the persisted `ReminderDefinition.Delivery.Address` equals
  `C0123ABC`
- **AND** `Delivery.Transport` equals `"slack"`
- **AND** `Delivery.Kind` equals `Channel`
- **AND** the tool response reports success with the resolved schedule

#### Scenario: Unknown transport fails loud

- **GIVEN** a host with only a `slack` resolver registered
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"`, `delivery.transport = "discord"`,
  `delivery.address = "#general"`
- **THEN** the tool returns an error string naming the unknown
  transport and listing available transports
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Unresolvable address returns actionable tool error

- **GIVEN** a host with a registered `slack` resolver that cannot
  resolve `#nonexistent-channel`
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"`, `delivery.transport = "slack"`,
  `delivery.address = "#nonexistent-channel"`
- **THEN** the tool returns an error string beginning with
  `Error: Could not resolve`
- **AND** no `ReminderDefinition` is persisted
- **AND** no `SaveReminderCommand` is sent to the reminder manager

#### Scenario: CurrentSession without addressable context rejected

- **GIVEN** a tool execution context with `ChannelType = Headless` (or
  `Webhook`, `Reminder`) and a non-null `SessionId`
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "current_session"`
- **THEN** the tool returns an error explaining that CurrentSession is
  only supported for channels with a `DeliverTrustedSessionTurn`
  gateway (Slack, Tui, SignalR)
- **AND** no `ReminderDefinition` is persisted

#### Scenario: CurrentSession rejects channel fields

- **GIVEN** an addressable Slack session context
- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "current_session"` AND either
  `delivery.transport` or `delivery.address` non-null
- **THEN** the tool returns an error stating that transport/address
  are invalid for CurrentSession
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Channel without transport rejected

- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"` and `delivery.transport` or
  `delivery.address` missing
- **THEN** the tool returns an error naming which required field is
  missing
- **AND** no `ReminderDefinition` is persisted

#### Scenario: Channel kind rejected for session-only transports

- **WHEN** the LLM calls `set_reminder` with
  `delivery.kind = "channel"` and `delivery.transport = "signalr"` (or
  `"tui"`)
- **THEN** the tool returns an error explaining that SignalR/TUI do
  not support channel-target delivery, and advising the LLM to use
  `delivery.kind = "current_session"`
- **AND** no `ReminderDefinition` is persisted

#### Scenario: None delivery rejects transport and address

- **WHEN** the LLM calls `set_reminder` with `delivery.kind = "none"`
  and either `delivery.transport` or `delivery.address` non-null
- **THEN** the tool returns an error stating that transport/address
  are invalid for None
- **AND** no `ReminderDefinition` is persisted

### Requirement: Stale reminder schema hard-delete on startup

`ReminderDefinitionStore` SHALL attempt to deserialize each persisted
reminder under the current `ReminderDefinition` schema at startup.
Rows that fail deserialization SHALL be deleted from disk (hard
delete) and SHALL NOT be restored as scheduled Akka.Reminders entries.
The store SHALL emit an `OperationalAlert` at `Warning` severity
listing the IDs of dropped reminders so the operator can re-create
them.

Netclaw is pre-production with a single operator; no in-place
migration is required. This requirement is authoritative for the
reminder-delivery-contract schema break.

#### Scenario: Stale reminder dropped at startup

- **GIVEN** `netclaw.db` contains a `netclaw_reminders` row with a
  pre-`reminder-delivery-contract` shape (populated
  `ReportToChannel` / `NotifyInstructions`, no `Delivery` struct)
- **WHEN** the daemon starts and `ReminderDefinitionStore` loads
- **THEN** the row is deleted from `netclaw_reminders`
- **AND** no Akka.Reminders schedule is created for it
- **AND** an `OperationalAlert` is emitted at `Warning` severity
  naming the dropped reminder ID and the reason
- **AND** the daemon startup completes successfully

### Requirement: Transport-keyed reminder target resolver registration

`IReminderTargetResolver` SHALL expose a `Transport` property (string)
identifying the channel transport the resolver owns (e.g., `"slack"`).
Hosts SHALL register resolver implementations via DI such that
`SetReminderTool` receives them as `IEnumerable<IReminderTargetResolver>`
and SHALL dispatch `delivery.address` resolution to the resolver whose
`Transport` matches `delivery.transport` (case-insensitive).

Hosts SHALL NOT register two resolvers with the same `Transport`
value; if this is detected at startup, host initialization SHALL fail
loud.

`SetReminderTool` SHALL reject `delivery.kind = Channel` with an
unknown or unregistered `delivery.transport` at tool-call time with an
error enumerating the registered transports.

#### Scenario: Slack resolver registered and keyed correctly

- **GIVEN** a host with `SlackReminderTargetResolver` registered
- **WHEN** `set_reminder` is called with
  `delivery.transport = "slack"`, `delivery.address = "#general"`
- **THEN** the Slack resolver's `ResolveAsync` is invoked with
  `"#general"`
- **AND** the returned canonical ID is persisted as
  `Delivery.Address`

#### Scenario: Duplicate-transport registration fails startup

- **GIVEN** a host that registers two `IReminderTargetResolver`
  instances both reporting `Transport = "slack"`
- **WHEN** the daemon starts
- **THEN** host initialization fails with an error naming the duplicate
  transport
- **AND** the daemon does not enter the ready state

#### Scenario: Unknown transport rejected with available-transport list

- **GIVEN** a host with only a `slack` resolver registered
- **WHEN** `set_reminder` is called with
  `delivery.transport = "discord"`
- **THEN** the tool returns an error naming `"discord"` as unknown and
  listing `["slack"]` as the registered transports
- **AND** no reminder is persisted

### Requirement: Execution outcome controls occurrence acknowledgement

Netclaw SHALL pass the Akka.Reminders envelope to every reminder execution. Netclaw SHALL acknowledge an occurrence only after successful execution and required delivery.

Netclaw SHALL send a negative acknowledgement after a known execution or delivery failure. The negative acknowledgement SHALL use the library retry budget.

The reminder manager SHALL accept the execution result before the child stops. DeathWatch SHALL report failure only before result acceptance.

#### Scenario: Channel execution fails before delivery

- **GIVEN** an enabled channel reminder occurrence is awaiting acknowledgement
- **WHEN** its session fails before required delivery succeeds
- **THEN** Netclaw sends a negative acknowledgement with the failure reason
- **AND** Netclaw does not send a successful acknowledgement
- **AND** Akka.Reminders persists the next attempt or a terminal state

#### Scenario: Execution and required delivery succeed

- **GIVEN** an enabled reminder occurrence is awaiting acknowledgement
- **WHEN** execution and required delivery succeed
- **THEN** Netclaw acknowledges the exact occurrence
- **AND** Akka.Reminders records `Delivered`

### Requirement: Reminder-level poison state is durable

Netclaw SHALL persist a consecutive execution failure count in the reminder definition. Each failed attempt SHALL increment the count, and a successful attempt SHALL reset it.

Netclaw SHALL disable the complete reminder when the count reaches `FailurePauseThreshold`. This count SHALL remain separate from the Akka.Reminders per-occurrence attempt count.

#### Scenario: Restart preserves the poison count

- **GIVEN** a reminder has three consecutive failed attempts
- **WHEN** the daemon restarts
- **THEN** reminder status reports three consecutive failures
- **AND** the next failed attempt increments the count to four

#### Scenario: Success resets the poison count

- **GIVEN** a reminder has one or more consecutive failed attempts
- **WHEN** a later attempt succeeds
- **THEN** Netclaw persists a zero consecutive failure count

#### Scenario: Fifth failure disables the complete reminder

- **GIVEN** a reminder has four consecutive failed attempts
- **WHEN** the next attempt fails
- **THEN** Netclaw disables the reminder
- **AND** Netclaw records a failed terminal outcome
- **AND** Netclaw cancels future occurrences for the complete reminder

### Requirement: One-shot reminders have one terminal settlement

Netclaw SHALL settle each one-shot reminder exactly once. The settlement SHALL end the one-shot. To end a one-shot, Netclaw SHALL record a reminder tombstone (see the [engineering glossary](../../../docs/spec/GLOSSARY.md#reminder-tombstone)) and then delete the definition and its schedule.

A one-shot SHALL end at the first of these events, with this outcome:

- a successful run: `Completed`
- a run with a denied tool call: `Failed`, with the denial text
- a failed run that reaches `FailurePauseThreshold`: `Failed`
- a failed run that Akka.Reminders does not retry (the negative acknowledgement replies `Failed` or `Expired`): `Failed`
- a fire after its `ExpiresAt`: `Expired`, and the reminder does not run
- a cancel: `Cancelled`
- a disabled one-shot whose fire time passed: `Cancelled`

Below the threshold, Netclaw SHALL keep a failed one-shot enabled so Akka.Reminders can retry it. A failed run below the threshold SHALL NOT end the one-shot.

A recurring reminder that reaches the threshold SHALL stay as a definition, disabled, with the `Failed` outcome. Netclaw SHALL NOT delete a recurring reminder on its own. Only an explicit delete command removes it.

The tombstone SHALL hold the id, the title, the audience, the fire time of the ended occurrence, the outcome, the end time, and the last error. A tombstone with the outcome `Failed` SHALL also hold the reminder instructions (the prompt). A tombstone with any other outcome SHALL NOT hold the instructions. No tombstone SHALL hold the delivery target, the delivery instructions, or the boundary. Netclaw SHALL delete the history and then the tombstone when the end time is more than 12 days old. It SHALL check at startup reconciliation and every 12 hours.

The failed count reported by `netclaw stats` SHALL count only enabled reminders with at least one consecutive failure.

#### Scenario: Successful one-shot is retained, then pruned

- **GIVEN** a one-shot reminder `check-pr-42` fires at 10:00
- **WHEN** its run succeeds and Netclaw settles the occurrence
- **THEN** the definition and the schedule of `check-pr-42` no longer exist
- **AND** a tombstone records the outcome `Completed` and the end time
- **AND** reminder history for `check-pr-42` returns the successful record
- **AND** Netclaw removes the history and the tombstone once the retention period has passed

#### Scenario: A current-session one-shot ends at admission

- **GIVEN** a one-shot reminder with `delivery_kind = current_session`
- **WHEN** the target session acknowledges the fire
- **THEN** the one-shot ends with the outcome `Completed`
- **AND** the end does not wait for the session turn

#### Scenario: A failed end keeps the prompt

- **GIVEN** a one-shot with the instructions "Check PR 42 and tell me when CI is green"
- **WHEN** the one-shot ends with the outcome `Failed`
- **THEN** the tombstone holds the instructions
- **AND** the tombstone does not hold the delivery target

#### Scenario: Other ends do not keep the prompt

- **GIVEN** three one-shots that end as `Completed`, `Cancelled`, and `Expired`
- **WHEN** Netclaw writes their tombstones
- **THEN** no tombstone holds the instructions

#### Scenario: Failed one-shot remains enabled for retry

- **GIVEN** a one-shot attempt fails for the second time
- **WHEN** Netclaw settles the failed attempt
- **THEN** the definition stays enabled
- **AND** no tombstone exists for the id
- **AND** reminder status shows the durable attempt state

#### Scenario: Poisoned one-shot remains inspectable

- **GIVEN** a one-shot has four consecutive failed attempts
- **WHEN** the fifth attempt fails
- **THEN** the definition and the schedule no longer exist
- **AND** the tombstone records the outcome `Failed` and the last error
- **AND** Netclaw emits the critical `reminder.auto_disabled` alert
- **AND** all five failure records stay readable through reminder history

#### Scenario: Akka.Reminders stops the retries

- **GIVEN** a one-shot attempt fails below the threshold
- **WHEN** the negative acknowledgement replies `Expired`
- **THEN** the one-shot ends with the outcome `Failed`

#### Scenario: A denied tool call ends the one-shot

- **GIVEN** a one-shot run calls a tool that authorization denies
- **WHEN** Netclaw settles the run
- **THEN** Netclaw acknowledges the occurrence and does not retry it
- **AND** the one-shot ends with the outcome `Failed` and the denial text
- **AND** the history row has the status `denied`

#### Scenario: An expired one-shot ends without a run

- **GIVEN** a restart-resume one-shot whose `ExpiresAt` is in the past
- **WHEN** its fire arrives
- **THEN** no execution starts
- **AND** the one-shot ends with the outcome `Expired`

#### Scenario: Failed reminders are kept but stop counting in stats

- **GIVEN** a cron reminder has four consecutive failed attempts
- **WHEN** the fifth attempt fails
- **THEN** the definition stays, disabled, with the outcome `Failed`
- **AND** no tombstone exists for the id
- **AND** the failed count in `netclaw stats` does not include it

#### Scenario: Reconciliation uses durable occurrence state

- **GIVEN** an enabled one-shot has a past fire time
- **WHEN** reconciliation or the 12-hour sweep finds no active schedule
- **THEN** the pass reads the durable occurrence state
- **AND** a terminal state ends the one-shot with the matching outcome
- **AND** a state that is not terminal leaves the occurrence to Akka.Reminders
- **AND** a missing state keeps the definition enabled for operator review

#### Scenario: The tombstone and the history are purged after 12 days

- **GIVEN** a one-shot ended at 10:00 on 1 October
- **WHEN** the sweep runs at 10:01 on 13 October
- **THEN** the history file and the tombstone no longer exist
- **AND** reminder history for the id replies "not found"

#### Scenario: The tombstone is kept inside the retention period

- **GIVEN** a one-shot ended at 10:00 on 1 October
- **WHEN** the sweep runs at 09:59 on 13 October
- **THEN** the tombstone and the history stay

### Requirement: Reminder attempts have bounded acknowledgement leases

Netclaw SHALL use a one-hour absolute execution limit and a 70-minute Akka.Reminders acknowledgment timeout. It SHALL retain the 20-minute inactivity limit.

#### Scenario: Valid long execution completes within the lease

- **GIVEN** a reminder execution produces activity and completes within one hour
- **WHEN** required delivery succeeds
- **THEN** Netclaw acknowledges the occurrence before its 70-minute deadline

#### Scenario: Execution reaches the absolute limit

- **GIVEN** a reminder execution remains active for one hour
- **WHEN** the absolute limit expires
- **THEN** Netclaw stops the attempt
- **AND** Netclaw sends a negative acknowledgement

#### Scenario: The remaining lease cannot contain an attempt

- **GIVEN** an occurrence has less than the maximum attempt duration plus the settlement margin remaining
- **WHEN** Netclaw considers the occurrence for execution
- **THEN** Netclaw does not start the execution
- **AND** Netclaw settles the occurrence by its one-shot or reminder-series blocked-occurrence policy

### Requirement: Blocked occurrence settlement remains bounded

An occurrence SHALL be blocked when another execution is already active for the same reminder, or when its remaining acknowledgement lease is shorter than the absolute execution limit plus the settlement margin.

Netclaw SHALL NOT retain a blocked Akka.Reminders envelope in an in-memory catch-up queue.

Netclaw SHALL negatively acknowledge a blocked one-shot occurrence. Netclaw SHALL acknowledge and skip a blocked reminder-series occurrence.

Netclaw SHALL ignore an exact duplicate of the active occurrence. The active execution SHALL remain the sole settlement owner.

#### Scenario: One-shot occurrence is blocked

- **GIVEN** a one-shot occurrence cannot start because another execution is active or its remaining lease is too short
- **WHEN** the manager handles the occurrence
- **THEN** the manager sends a negative acknowledgement
- **AND** Akka.Reminders owns the retry delay

#### Scenario: Reminder-series occurrence is blocked

- **GIVEN** a reminder-series occurrence cannot start for the same reason
- **WHEN** the manager handles the occurrence
- **THEN** the manager acknowledges the occurrence without execution
- **AND** Netclaw does not retain the occurrence for catch-up work

#### Scenario: Exact active occurrence arrives again

- **GIVEN** an occurrence already has an active execution
- **WHEN** the same key, due time, and acknowledgement deadline arrive again
- **THEN** Netclaw does not start or settle the duplicate envelope
- **AND** the active execution remains the sole settlement owner

### Requirement: Settlement write order supports recovery

Netclaw SHALL save a failed run and its poison count before it sends a negative acknowledgement. Netclaw SHALL not advance Akka state after a local save failure.

Netclaw SHALL write the tombstone of a one-shot before every other step of its end: before the acknowledgement, the negative acknowledgement after a threshold failure, the schedule cancel, and the definition delete. The only exception is a failure that is terminal only because of the negative acknowledgement reply. Then the tombstone SHALL come right after that reply.

If Netclaw cannot write the tombstone of a one-shot after a run that succeeded or had a denied tool call, it SHALL still acknowledge the occurrence. The acknowledgement records the durable status `Delivered`, so Akka.Reminders does not deliver the occurrence again, and the run does not repeat. Netclaw SHALL keep the definition, log an error, and emit the `reminder.settlement.failed` alert. The next startup reconciliation or the next 12-hour sweep, whichever comes first, SHALL end the one-shot from the durable status. A running daemon SHALL NOT need a restart. The pass SHALL finish well before Akka.Reminders deletes the completed row, which happens 12 days after the end.

- A one-shot with the durable status `Delivered` ends as `Completed`, with one exception. The pass SHALL use the history row of this occurrence: the newest row, only when its `FiredAt` is not earlier than the fire time of the definition. If that row has the status `denied`, the pass SHALL end the one-shot as `Failed`. The last error SHALL be the error text of that row, and the tombstone SHALL hold the instructions (the prompt). A `denied` row of an earlier run, from before a re-arm, SHALL NOT change the outcome.
- If the pass finds no readable history row of this occurrence (the file is damaged or has no row for it), it SHALL still end a `Delivered` one-shot as `Completed`. It SHALL log a warning that names the reminder and says that its history cannot be read, because the denial check could not run.
- A one-shot with the durable status `Failed`, `Expired`, or `Cancelled` ends as `Failed`.
- A one-shot with the status `Pending` or `AwaitingAck` has an active or waiting occurrence. The pass SHALL leave it alone.
- The pass SHALL NOT query the status of a one-shot that has an active run in the manager. An active run settles itself, and the occurrence of an active run is open.
- If the status query of a one-shot fails (an `Error` response or a failed Ask), the pass SHALL catch the failure. The manager SHALL NOT restart, because a restart stops every execution actor, and an open occurrence can then run again after the acknowledgement lease. The pass SHALL log an error that names the reminder and says "occurrence status". It SHALL emit the `reminder.status.query_failed` alert with the reminder id as the source. It SHALL leave that one-shot unchanged and go on to the next one-shot. The next pass tries again.
- If the tombstone file of the one-shot exists but cannot be read, the pass SHALL skip that one-shot. It SHALL NOT overwrite the file and SHALL NOT end the one-shot. It SHALL log an error and emit the `reminder.tombstone.invalid` alert. The next pass tries again. The status pass runs inside the sweep, after the sweep has matched or dropped every readable tombstone of the one-shot. A tombstone file that still exists at that point therefore cannot be read. This rule depends on that order.

A damaged tombstone file can hold a `Completed` record. The runbook SHALL tell the operator to read `<id>.history.jsonl` before the operator deletes the file. If the history file has an `ok` row for this fire time, the operator SHALL repair the file with `outcome: Completed` and SHALL NOT delete it. A delete inside the attempt window lets the redelivery run the one-shot again. A delete after the window ends the one-shot as `Failed` with its prompt, although the one-shot ran.

If Netclaw cannot write the tombstone of any other end (a cancel, a failure at the threshold, or an expiry), it SHALL NOT acknowledge or negatively acknowledge the occurrence, cancel the schedule, or delete the definition. It SHALL emit a settlement failure alert. The acknowledgement lease SHALL stay the recovery path.

A crash at any step after the tombstone SHALL NOT run the one-shot again and SHALL NOT lose its outcome. Reconciliation SHALL repair one-shot terminal state after a post-acknowledgement process failure.

#### Scenario: Local failure state cannot be saved

- **GIVEN** an execution attempt fails
- **WHEN** Netclaw cannot save its poison state
- **THEN** Netclaw does not send a negative acknowledgement
- **AND** the Akka.Reminders acknowledgement timeout remains the recovery path

#### Scenario: The tombstone cannot be written after a successful or denied run

- **GIVEN** a one-shot run succeeds, or a tool call of the run is denied
- **WHEN** the tombstone write fails with an I/O error
- **THEN** Netclaw acknowledges the occurrence, and the durable status is `Delivered`
- **AND** the definition stays enabled
- **AND** Netclaw logs an error and emits the `reminder.settlement.failed` alert
- **AND** the one-shot does not run again, also after the acknowledgement lease time passes
- **AND** the next startup reconciliation or the next 12-hour sweep ends the one-shot and deletes the definition
- **AND** the end is `Completed` for a success, and `Failed` for a denied tool call (see the scenarios below)

#### Scenario: The sweep ends the one-shot without a restart

- **GIVEN** a one-shot run succeeded, the tombstone write failed, and Netclaw acknowledged the occurrence
- **AND** the disk works again, and the daemon does not restart
- **WHEN** the 12-hour sweep runs
- **THEN** the one-shot ends as `Completed`, the tombstone exists, and the definition no longer exists
- **AND** the one-shot does not run again, also at the next sweep

#### Scenario: The sweep leaves an active run alone

- **GIVEN** a one-shot run is active, and its durable status is `AwaitingAck`
- **WHEN** the 12-hour sweep runs
- **THEN** the definition stays, and no tombstone exists
- **AND** when the run succeeds, the one-shot ends as `Completed`

#### Scenario: A denied run ends as `Failed` after the tombstone write failed

- **GIVEN** a tool call of a one-shot run was denied, the tombstone write failed, and Netclaw acknowledged the occurrence
- **AND** the history row of this run has the status `denied`
- **WHEN** the next startup reconciliation or the next 12-hour sweep runs
- **THEN** the one-shot ends as `Failed`
- **AND** the last error of the tombstone holds the denial text
- **AND** the tombstone holds the instructions

#### Scenario: A successful run still ends as `Completed` after the tombstone write failed

- **GIVEN** a one-shot run succeeded, the tombstone write failed, and Netclaw acknowledged the occurrence
- **AND** the history row of this run has the status `ok`
- **WHEN** the next startup reconciliation or the next 12-hour sweep runs
- **THEN** the one-shot ends as `Completed`
- **AND** the tombstone holds no instructions and no error

#### Scenario: An unreadable tombstone stops the end from the durable status

- **GIVEN** a one-shot has the durable status `Delivered`, and its definition still exists
- **AND** a tombstone file for the id exists that Netclaw cannot read
- **WHEN** the startup reconciliation or the 12-hour sweep runs
- **THEN** the definition and the file stay unchanged
- **AND** Netclaw logs an error and emits the `reminder.tombstone.invalid` alert
- **AND** the one-shot does not run

#### Scenario: A delete after the attempt window ends a one-shot that never ran as `Failed`

- **GIVEN** an unreadable tombstone stopped every fire of a one-shot until Akka.Reminders used its last attempt
- **AND** the durable status is now `Failed`
- **AND** the history file has no row for this fire time, so the one-shot never ran
- **WHEN** the operator deletes the file and the next pass runs
- **THEN** the one-shot ends as `Failed`, and the tombstone holds the instructions
- **AND** the one-shot did not run

#### Scenario: A delete of a tombstone that held a `Completed` record loses the outcome

- **GIVEN** a one-shot ran, and its tombstone file with `outcome: Completed` was damaged afterwards
- **AND** the history file has an `ok` row for this fire time
- **AND** the attempt window has passed, so the durable status is `Failed`
- **WHEN** the operator deletes the file instead of repairing it, and the next pass runs
- **THEN** the one-shot ends as `Failed` with its prompt, although it ran
- **AND** the runbook told the operator to read the history file before a delete

#### Scenario: A repair with the `Completed` outcome keeps the outcome

- **GIVEN** the same state, with the history row `ok` for this fire time
- **WHEN** the operator repairs the file with `outcome: Completed` and the same fire time
- **THEN** the next pass or the next fire finishes the end and deletes the definition
- **AND** the tombstone keeps the outcome `Completed`, and the one-shot does not run again

#### Scenario: A repaired tombstone lets the end from the durable status happen

- **GIVEN** an unreadable tombstone stopped the end of a one-shot with the durable status `Delivered`
- **WHEN** the operator deletes the file and the next pass runs
- **THEN** the one-shot ends as `Completed`

#### Scenario: A failed status query leaves that one-shot alone

- **GIVEN** a one-shot ran, the tombstone write failed, and Netclaw acknowledged the occurrence
- **AND** no run of the one-shot is active
- **AND** the status query for the one-shot fails
- **WHEN** the 12-hour sweep runs
- **THEN** the manager does not restart
- **AND** Netclaw logs an error that names the reminder, and emits the `reminder.status.query_failed` alert with the reminder id as the source
- **AND** the definition stays enabled, and no tombstone is written
- **AND** the next sweep, after the store recovers, ends the one-shot as `Completed`
- **AND** the one-shot ran once

#### Scenario: A failed status query does not stop the pass

- **GIVEN** two such one-shots, and the status query for the first one fails
- **WHEN** the sweep runs
- **THEN** the pass ends the second one-shot in the same sweep
- **AND** the alert names the first one-shot, and the next sweep ends it

#### Scenario: A one-shot with an active run needs no status query

- **GIVEN** a one-shot run is active, and every status query would fail
- **WHEN** the 12-hour sweep runs
- **THEN** the pass sends no status query for the one-shot
- **AND** Netclaw emits no `reminder.status.query_failed` alert, and the manager does not restart
- **AND** the run continues and settles once, and the one-shot does not run again after the acknowledgement lease time

#### Scenario: A failed status query at startup does not stop the reconciliation

- **GIVEN** an orphan schedule, a future one-shot without a schedule, an expired tombstone with its history, and a past one-shot whose status query fails
- **WHEN** the startup reconciliation runs
- **THEN** it cancels the orphan schedule, restores the schedule, and purges the expired tombstone and its history
- **AND** the reconciliation replies `ReconcileCompleted`, not a failure
- **AND** Netclaw emits the `reminder.status.query_failed` alert for the past one-shot, which stays enabled

#### Scenario: A denied row of an earlier run does not fail a re-armed one-shot

- **GIVEN** a denied run left no tombstone, and the agent re-armed the same id with a later fire time
- **AND** Netclaw acknowledged the new occurrence, but the history row of the new run was not written
- **AND** the newest history row is the `denied` row of the earlier run, and its `FiredAt` is before the new fire time
- **WHEN** the next startup reconciliation runs
- **THEN** the pass ignores the `denied` row
- **AND** the one-shot ends as `Completed`, and the tombstone holds no instructions and no error

#### Scenario: A damaged history file ends the one-shot from the status and logs a warning

- **GIVEN** a one-shot run succeeded, the tombstone write failed, and Netclaw acknowledged the occurrence
- **AND** the history file of the one-shot has a partial last line
- **WHEN** the next startup reconciliation or the next 12-hour sweep runs
- **THEN** the one-shot ends as `Completed`
- **AND** Netclaw logs a warning that names the reminder and says that its history cannot be read

#### Scenario: A readable history file logs no history warning

- **GIVEN** the same state with an intact `ok` row
- **WHEN** the next startup reconciliation or the next 12-hour sweep runs
- **THEN** the one-shot ends as `Completed`
- **AND** Netclaw logs no warning about the history of the reminder

#### Scenario: The tombstone cannot be written for a cancel

- **GIVEN** a one-shot is scheduled
- **WHEN** a cancel cannot write the tombstone
- **THEN** the definition and the schedule stay
- **AND** Netclaw emits the `reminder.settlement.failed` alert
- **AND** the one-shot is not ended

#### Scenario: Process stops after the tombstone and before the acknowledgement

- **GIVEN** Netclaw wrote the `Completed` tombstone of a one-shot
- **AND** the process stopped before the acknowledgement
- **WHEN** Akka.Reminders delivers the occurrence again
- **THEN** no execution starts
- **AND** Netclaw acknowledges the occurrence and deletes the definition

#### Scenario: Process stops after successful acknowledgement

- **GIVEN** Netclaw acknowledged a terminal occurrence
- **AND** the process stopped before it wrote the tombstone
- **WHEN** the daemon starts, or the 12-hour sweep runs
- **THEN** the pass reads the durable occurrence status
- **AND** a `Delivered` status ends the one-shot as `Completed`, unless the history row of this occurrence is `denied` (then `Failed`)
- **AND** a `Failed`, `Expired`, or `Cancelled` status ends the one-shot as `Failed`

#### Scenario: Process stops after the tombstone and before the definition delete

- **GIVEN** a one-shot has a tombstone with the same fire time as its definition
- **WHEN** the daemon starts
- **THEN** reconciliation cancels the schedule and deletes the definition
- **AND** the tombstone stays unchanged

### Requirement: Fail-closed reminder write validation

Reminder write surfaces SHALL validate reminder audience server-side before
persisting or importing a reminder definition. This applies to REST, admin,
CLI, and import paths in addition to conversational tool calls. Invalid
audience values, missing required authority context, or requested audiences
that exceed the caller's source authority SHALL be rejected with clear error
messages. Execution may trust the stored reminder audience because minting-time
validation is mandatory.

#### Scenario: REST create rejects invalid audience value

- **GIVEN** a REST reminder create request provides `audience: "superuser"`
- **WHEN** the server validates the request
- **THEN** the request is rejected with a clear validation error
- **AND** no reminder definition is persisted

#### Scenario: Admin import rejects over-privileged reminder

- **GIVEN** an admin or import request is authenticated with source audience `Team`
- **WHEN** the request submits a reminder definition with stored audience `Personal`
- **THEN** the server rejects the request with a clear over-privilege error
- **AND** the reminder is not written to disk

#### Scenario: Write path fails closed without authority context

- **GIVEN** a non-conversational reminder write path cannot determine the caller's source audience / authority
- **WHEN** the request attempts to create or import a reminder definition
- **THEN** the server rejects the request
- **AND** the error states that reminder audience authorization context is required

#### Scenario: Execution trusts stored audience after validated minting

- **GIVEN** a reminder definition was accepted by the server's minting validation
- **WHEN** the reminder executes later on a timer
- **THEN** the execution path uses the stored audience as authoritative
- **AND** no deployment-default fallback broadens that audience

### Requirement: run_reminder agent tool

The system SHALL provide a `run_reminder` tool in the `scheduling` grant
category. The tool SHALL take a reminder ID and SHALL return the exact prompt
that the scheduled run of that reminder sends, with an instruction to carry it
out in the current chat. The tool SHALL NOT start a separate run, change the
schedule, or write a reminder history record.

The tool SHALL return the prompt only when all of these are true:

- the current turn has a person who can answer approval prompts;
- the reminder audience is at or below the caller audience (else the reminder
  reads as not found);
- the reminder audience equals the chat audience.

#### Scenario: Same-audience chat gets the exact prompt
- **WHEN** a Personal chat with interactive approval calls `run_reminder` for a Personal reminder
- **THEN** the result contains the same prompt text that the scheduled run sends
- **AND** the schedule and the reminder history do not change

#### Scenario: Wider chat is refused
- **WHEN** a Personal chat calls `run_reminder` for a Team reminder
- **THEN** the result is an error that tells the user to run the test in a Team chat
- **AND** the result does not contain the reminder prompt

#### Scenario: Reminder above the caller reads as not found
- **WHEN** a Team chat calls `run_reminder` for a Personal reminder
- **THEN** the result is the same not-found error as for a missing ID

#### Scenario: Unattended caller is refused
- **WHEN** a turn with no interactive approval (a reminder, a webhook, or headless chat) calls `run_reminder`
- **THEN** the result is an error and contains no prompt

#### Scenario: Grant saved in the chat test lets the scheduled run pass
- **GIVEN** a Personal reminder whose prompt runs a shell command that needs approval in a folder outside the trusted roots
- **WHEN** the user runs the prompt in a Personal chat after `run_reminder` and answers "Always here"
- **AND** the reminder later fires unattended with the same command
- **THEN** the scheduled run reads the saved grant and runs the command with no prompt

#### Scenario: Scheduled run without a saved grant is denied
- **WHEN** the same reminder fires unattended and no grant covers the command
- **THEN** the scheduled run denies the command

### Requirement: Reminder test entry points

The system SHALL provide a `run-reminder` system skill. `/run-reminder <id>`
SHALL load it. The skill SHALL warn the user that the steps are real before
it calls `run_reminder`. The CLI command `netclaw reminder run <id>` SHALL open
a normal chat whose hidden first message is `/run-reminder <id>`.

#### Scenario: CLI opens a test chat
- **WHEN** the operator runs `netclaw reminder run disk-check`
- **THEN** the CLI opens the chat page on a new session
- **AND** the session receives `/run-reminder disk-check` as its first message

#### Scenario: CLI usage error
- **WHEN** the operator runs `netclaw reminder run` with no ID
- **THEN** the CLI prints `Usage: netclaw reminder run <id>` and exits non-zero
### Requirement: A cancel deletes a one-shot reminder

A cancel of a one-shot reminder SHALL end it with the outcome `Cancelled`. This applies to `cancel_reminder`, `netclaw reminder cancel`, and `DELETE /api/reminders/{id}` without `permanent`. Netclaw SHALL write the tombstone before it cancels the schedule. A cancel SHALL NOT stop a run that is active. A cancel of a recurring reminder SHALL disable it and keep it.

A cancel of an ended one-shot SHALL change nothing. It SHALL report that the reminder already ended, with the outcome and the end time. The audience gate SHALL apply to the cancel of an ended one-shot. Outside the caller's audience, the reply SHALL be "not found".

`netclaw reminder disable` SHALL stay a reversible pause for a one-shot. A disabled one-shot with a future fire time SHALL stay as a definition.

#### Scenario: Cancel deletes a one-shot

- **GIVEN** an enabled one-shot reminder `deploy-check` fires in two hours
- **WHEN** the agent calls `cancel_reminder` with `deploy-check`
- **THEN** the definition and the schedule no longer exist
- **AND** the tombstone records the outcome `Cancelled`
- **AND** the tool replies that the reminder was cancelled and deleted

#### Scenario: Cancel keeps a recurring reminder

- **GIVEN** an enabled interval reminder `ci-watch`
- **WHEN** the agent calls `cancel_reminder` with `ci-watch`
- **THEN** the definition stays, disabled
- **AND** no tombstone exists for `ci-watch`

#### Scenario: A process stop after the cancel tombstone does not run the reminder

- **GIVEN** a cancel wrote the tombstone of `deploy-check`
- **AND** the process stopped before the schedule cancel
- **WHEN** the daemon starts, or the fire arrives first
- **THEN** no execution starts
- **AND** the schedule and the definition no longer exist

#### Scenario: Cancel of an ended one-shot

- **GIVEN** `check-pr-42` ended as `Completed` at 10:00
- **WHEN** a caller in the same audience cancels `check-pr-42`
- **THEN** the reply says that the reminder already ended (completed at 10:00)
- **AND** the tombstone and the history do not change

#### Scenario: Cancel of an ended one-shot outside the audience

- **GIVEN** a Personal one-shot ended
- **WHEN** a Team caller cancels it
- **THEN** the reply is "not found", the same as for an id that never existed

#### Scenario: A paused one-shot with a future fire time stays

- **GIVEN** an operator ran `netclaw reminder disable` on a one-shot that fires tomorrow
- **WHEN** the sweep runs today
- **THEN** the definition stays, disabled
- **AND** `netclaw reminder enable` can schedule it again

#### Scenario: A paused one-shot ends when its fire time passes

- **GIVEN** a disabled one-shot whose fire time was yesterday
- **WHEN** the sweep runs
- **THEN** the one-shot ends with the outcome `Cancelled`

### Requirement: Settlement ends only the occurrence that it ran

Before it ends a one-shot, Netclaw SHALL check that the current definition is the one that ran. The fire time of the definition SHALL equal the due time of the settled occurrence, at millisecond precision. If the times differ, Netclaw SHALL record the history row and settle the occurrence, and it SHALL NOT change the definition. If no definition exists, Netclaw SHALL record the history row and settle the occurrence, and it SHALL NOT replace an existing tombstone.

#### Scenario: A watcher that sets itself again during its run keeps the new reminder

- **GIVEN** a `channel` one-shot `ci-poll` with the fire time 10:00 runs
- **AND** during the run, `set_reminder` replaces `ci-poll` with the fire time 10:10
- **WHEN** the 10:00 run succeeds and Netclaw settles it
- **THEN** the `ci-poll` definition with the fire time 10:10 stays enabled and scheduled
- **AND** no tombstone exists for `ci-poll`
- **AND** the history has a row for the 10:00 run

#### Scenario: The settled occurrence matches the definition

- **GIVEN** a one-shot with the fire time 10:00 runs
- **AND** nothing replaces its definition
- **WHEN** Netclaw settles the 10:00 run
- **THEN** the one-shot ends

#### Scenario: A cancel during the run wins

- **GIVEN** a one-shot run is active
- **AND** a cancel ends the one-shot as `Cancelled`
- **WHEN** the run succeeds and Netclaw settles it
- **THEN** the tombstone keeps the outcome `Cancelled`
- **AND** the history has a row for the run

### Requirement: A fire runs only the occurrence of the current definition

Before it runs, expires, or ends a one-shot for a fire, Netclaw SHALL check that the due time of the fire equals the fire time of the current definition, at millisecond precision. If the times differ, the occurrence belongs to an earlier definition of the same id. Netclaw SHALL NOT run it. Netclaw SHALL acknowledge that occurrence and log a warning. Netclaw SHALL NOT change the definition or its tombstone, and SHALL NOT cancel the schedule of the current definition.

Netclaw SHALL apply this check before every other rule for a fire. In particular, it applies before the rule for an unreadable tombstone. An occurrence of an earlier definition SHALL be acknowledged even when the tombstone file of the current definition cannot be read.

#### Scenario: An occurrence of an earlier definition does not run early

- **GIVEN** a one-shot `ci-poll` with the fire time 10:10
- **AND** Akka.Reminders still holds an occurrence of an earlier `ci-poll` due at 10:00
- **WHEN** the 10:00 occurrence fires
- **THEN** no execution starts
- **AND** Netclaw acknowledges the 10:00 occurrence and logs a warning
- **AND** the `ci-poll` definition stays enabled, and its 10:10 schedule stays
- **AND** the run starts once, at 10:10

#### Scenario: An occurrence of an earlier definition is acknowledged although the tombstone is unreadable

- **GIVEN** a one-shot `ci-poll` with the fire time 10:10
- **AND** a tombstone file for `ci-poll` that Netclaw cannot read
- **AND** Akka.Reminders still holds an occurrence of an earlier `ci-poll` due at 10:00
- **WHEN** the 10:00 occurrence fires
- **THEN** no execution starts
- **AND** Netclaw acknowledges the 10:00 occurrence
- **AND** Netclaw does not emit the `reminder.tombstone.invalid` alert for this fire
- **AND** the file and the definition stay unchanged

#### Scenario: An occurrence of the current definition runs

- **GIVEN** a one-shot `ci-poll` with the fire time 10:00
- **WHEN** the 10:00 occurrence fires
- **THEN** the execution starts

### Requirement: A tombstone that matches its definition wins

A tombstone SHALL match a definition when both have the same id, the definition is a one-shot, and both fire times are equal at millisecond precision. A matching tombstone SHALL mean that the one-shot ended.

- A fire for a definition with a matching tombstone SHALL NOT run. Netclaw SHALL acknowledge it, cancel the schedule, and delete the definition.
- A tombstone that does not match its definition SHALL be stale. Netclaw SHALL keep the definition, delete the stale tombstone, and log a warning.
- A fire for a one-shot whose tombstone file exists but cannot be read SHALL NOT run. Netclaw SHALL NOT acknowledge the occurrence. It SHALL keep the definition, the schedule, and the file. It SHALL log an error and emit the `reminder.tombstone.invalid` alert. The operator repairs or deletes the file, and the acknowledgement lease then returns the occurrence. The operator SHALL repair the file within about 12 hours. Akka.Reminders attempts the delivery ten times, with a 70-minute lease each. Then it marks the occurrence `Failed`, and the occurrence does not return. The next reconciliation or sweep then ends the one-shot as `Failed` from the durable status, once the file is readable or gone. That pass never overwrites an unreadable file (see "Settlement write order supports recovery"). A read of history or status still counts the unreadable file as absent (see `reminder-execution-history`).

#### Scenario: A matching tombstone blocks the run

- **GIVEN** a one-shot definition with the fire time 10:00
- **AND** a tombstone for the same id with the fire time 10:00
- **WHEN** the 10:00 fire arrives
- **THEN** no execution starts
- **AND** the definition no longer exists

#### Scenario: An unreadable tombstone stops the run

- **GIVEN** a one-shot definition with the fire time 10:00
- **AND** a tombstone file for the same id that Netclaw cannot read
- **WHEN** the 10:00 fire arrives
- **THEN** no execution starts
- **AND** Netclaw does not acknowledge the occurrence
- **AND** the definition, the schedule, and the file stay
- **AND** Netclaw logs an error and emits the `reminder.tombstone.invalid` alert

#### Scenario: A repaired tombstone lets the run happen

- **GIVEN** an unreadable tombstone stopped the 10:00 fire of a one-shot
- **WHEN** the operator deletes the file and the acknowledgement lease expires
- **THEN** Akka.Reminders delivers the occurrence again
- **AND** the execution starts once

#### Scenario: A stale tombstone does not block a newer definition

- **GIVEN** a one-shot definition with the fire time 11:00
- **AND** a tombstone for the same id with the fire time 10:00
- **WHEN** reconciliation runs
- **THEN** the definition stays enabled and scheduled
- **AND** the stale tombstone no longer exists
- **AND** Netclaw logs a warning that names the id

### Requirement: An ended one-shot id can be set again

A create or an update with the id of an ended one-shot SHALL start a new reminder. Netclaw SHALL first schedule the new occurrence. Then it SHALL delete the old history, delete the tombstone, and save the new definition. This applies to every write mode. If the schedule fails, the old history and the tombstone SHALL stay.

A tombstone outside the caller's audience SHALL block the id. The reply SHALL be the same "not found" reply as for a definition outside the caller's audience.

#### Scenario: Re-arm after the fire

- **GIVEN** `check-pr-42` ended as `Completed` and has one history row
- **WHEN** the agent calls `set_reminder` with the id `check-pr-42` and a new time
- **THEN** a new enabled definition with the new time exists
- **AND** the tombstone and the old history row no longer exist

#### Scenario: Re-arm discards the prompt of a failed one-shot

- **GIVEN** `check-pr-42` ended as `Failed` and its tombstone holds the instructions
- **WHEN** the agent calls `set_reminder` with the id `check-pr-42`
- **THEN** the tombstone no longer exists
- **AND** no file on disk holds the old instructions

#### Scenario: Restart recovery reuses an ended restart-resume id

- **GIVEN** a restart-resume one-shot ended
- **WHEN** restart recovery saves the same id with `CreateOnly`
- **THEN** the save succeeds and is not a conflict

#### Scenario: A tombstone outside the audience blocks the id

- **GIVEN** a Personal one-shot `nightly` ended
- **WHEN** a Team caller calls `set_reminder` with the id `nightly`
- **THEN** the save fails with "Reminder 'nightly' was not found."
- **AND** the tombstone and the history do not change

#### Scenario: A failed schedule keeps the old data

- **GIVEN** `check-pr-42` ended
- **WHEN** a re-arm gives a fire time in the past
- **THEN** the save fails
- **AND** the tombstone and the history stay

### Requirement: Read surfaces report an ended one-shot

Inside the retention period, the read surfaces SHALL report an ended one-shot to a caller in its audience:

- `list_reminders` and `netclaw reminder list` SHALL NOT show it.
- `netclaw reminder show <id>` and `GET /api/reminders/{id}` SHALL reply "not found" (HTTP 404, CLI exit code 1). The message SHALL name the outcome, the end time, and the time until which the history stays readable.
- `netclaw reminder status <id>` and `GET /api/reminders/{id}/status` SHALL reply with the outcome, the end time, the purge time, the last error, the durable occurrence status while it exists, and the recent history. For the outcome `Failed`, the reply SHALL also hold the reminder prompt. For the other outcomes, the reply SHALL NOT hold a prompt.
- `netclaw reminder enable|disable <id>` SHALL reply "not found" with the ended message.
- `run_reminder` SHALL reply with an error that says that the reminder ended.

The prompt of an ended one-shot SHALL be readable only through the status surfaces, and only for a caller inside the tombstone audience. `show`, `history`, `list`, and the agent tools SHALL NOT hold it.

Outside the caller's audience, every surface SHALL reply as for an id that never existed.

#### Scenario: Show on an ended one-shot

- **GIVEN** `check-pr-42` ended as `Completed` at 10:00 on 16 October
- **WHEN** the operator runs `netclaw reminder show check-pr-42`
- **THEN** the command exits with code 1
- **AND** the message says that the reminder ended (completed) at that time and names 28 October as the end of its history

#### Scenario: Status on an ended one-shot

- **GIVEN** `check-pr-42` ended as `Completed`
- **WHEN** the operator runs `netclaw reminder status check-pr-42`
- **THEN** the output shows the outcome `Completed`, the end time, and the recent history

#### Scenario: List does not show an ended one-shot

- **GIVEN** one enabled interval reminder and one ended one-shot
- **WHEN** the agent calls `list_reminders` with `filter = all`
- **THEN** the result shows only the interval reminder

#### Scenario: Status of a failed one-shot shows the prompt

- **GIVEN** `check-pr-42` ended as `Failed` with the instructions "Check PR 42 and tell me when CI is green"
- **WHEN** a caller inside the audience asks for its status
- **THEN** the reply holds the outcome `Failed`, the last error, and the instructions
- **AND** `netclaw reminder status check-pr-42` prints the instructions

#### Scenario: Status of a completed one-shot shows no prompt

- **GIVEN** `check-pr-42` ended as `Completed`
- **WHEN** a caller inside the audience asks for its status
- **THEN** the reply holds no instructions

#### Scenario: Status outside the audience

- **GIVEN** a Personal one-shot ended
- **WHEN** a Team caller asks for its status
- **THEN** the reply is "not found"

#### Scenario: The prompt of a failed one-shot stays inside its audience

- **GIVEN** a Personal one-shot ended as `Failed` and its tombstone holds the instructions
- **WHEN** a Team caller asks for its status, its history, or its details
- **THEN** every reply is "not found"
- **AND** no reply holds any part of the instructions

### Requirement: Reconciliation ends leftover one-shots

Startup reconciliation and the 12-hour sweep SHALL end every leftover one-shot definition. A leftover is a one-shot definition with a terminal outcome, or a disabled one-shot whose fire time has passed. The end time SHALL be the last update time of a definition with a terminal outcome. For a disabled one-shot without an outcome, the end time SHALL be the later of the last update time and the fire time. A leftover without a recorded outcome SHALL end as `Cancelled`, and its last error SHALL say that the outcome was not recorded. A leftover with the outcome `Failed` SHALL keep its instructions in its tombstone. The same pass SHALL purge each tombstone whose end time is more than 12 days old. The reconciliation log SHALL report the count of ended one-shots and the count of purged tombstones. Both passes SHALL also end an enabled one-shot with a past fire time from its durable occurrence status, under the rules of "Settlement write order supports recovery". A status that Akka.Reminders no longer holds SHALL keep the definition enabled for operator review. A failed status query for one one-shot SHALL NOT stop the rest of the reconciliation or of the sweep.

#### Scenario: Upgrade cleans old leftovers

- **GIVEN** an install with a `Completed` one-shot last updated 20 days ago, a `Failed` one-shot last updated 2 days ago, and a disabled one-shot without an outcome whose fire time was 30 days ago
- **WHEN** the new daemon starts
- **THEN** none of the three definitions exists
- **AND** the `Completed` and the disabled one-shot have no tombstone and no history
- **AND** the `Failed` one-shot has a tombstone that holds its instructions, and its history, until its 12 days pass
- **AND** the tombstone of a leftover that ended as `Completed` or `Cancelled` holds no instructions
- **AND** the reconciliation log line reports `ended_oneshots=3` and `purged_tombstones=2` (the leftovers that ended and were purged in the same pass count in both)

#### Scenario: Live reminders are not touched

- **GIVEN** an enabled one-shot with a future fire time, a disabled interval reminder, and a cron reminder with the outcome `Failed`
- **WHEN** reconciliation runs
- **THEN** all three definitions stay unchanged
- **AND** no tombstone exists for them
