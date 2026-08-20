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
  originating channel's existing inbound handling path. The daemon
  hosts two server-side gateways; both implement a
  `Receive<DeliverTrustedSessionTurn>` handler that reuses the
  gateway's existing routing code. The reminder dispatcher SHALL tell
  the appropriate gateway based on `Delivery.OriginChannelType`:
  `ChannelType.Slack` → `SlackGatewayActor`;
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
definition. A successful execution SHALL reset the count.

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

### Requirement: Envelope-ack-gated at-least-once delivery

The reminder manager SHALL retain each Akka.Reminders envelope until the attempt
has a known outcome. This rule SHALL apply to every delivery kind, so `Channel`
and `None` reminders no longer get an eager acknowledgement.

The execution actor SHALL report its outcome to the manager. It SHALL wait for
`ReminderExecutionAccepted` before it stops.

The manager SHALL acknowledge only a successful execution with all required
delivery evidence. It SHALL negatively acknowledge a known failure.

A `CurrentSession` reminder SHALL still use the origin gateway and
`Ask<CommandAck>`. Required delivery SHALL also wait for
`ReminderDeliveryResult`, which reports success and failure explicitly.

The target session SHALL keep its best-effort reminder key check. The key SHALL
use the stable occurrence due time.

#### Scenario: CurrentSession requires observed delivery

- **GIVEN** a `CurrentSession` reminder has `DeliveryRequired = true`
- **WHEN** the target session returns `CommandAck`
- **THEN** the execution remains incomplete
- **WHEN** a matching successful `ReminderDeliveryResult` arrives
- **THEN** the child reports success to the manager
- **AND** the manager acknowledges the occurrence

#### Scenario: CurrentSession delivery fails

- **GIVEN** a `CurrentSession` reminder awaits required delivery
- **WHEN** the gateway rejects the turn or delivery fails
- **THEN** the child reports a descriptive failure
- **AND** the manager sends a negative acknowledgement

#### Scenario: CurrentSession with DeliveryRequired=false acks on CommandAck alone

- **GIVEN** a `CurrentSession` reminder with `DeliveryRequired = false` fires
- **WHEN** `CommandAck` is received from the session
- **THEN** the child reports success to the manager
- **AND** no `ReminderDeliveryResult` wait is attempted

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

- **GIVEN** a `CurrentSession` reminder was previously processed by the session,
  evidenced by a `TurnRecorded` event whose `SourceReminderId` matches the
  reminder's `{reminderId}:{fireTimestampMs}` key and is present in
  `ProcessedReminderIds`
- **WHEN** Akka.Reminders redelivers the same envelope after a transient failure
- **THEN** the session dedup pre-check fires and `TryReplyAck()` returns
  `CommandAck` without re-processing the turn
- **AND** the manager settles the occurrence once, closing the redelivery loop

### Requirement: Reminder delivery guarantees

The reminder pipeline SHALL provide at-least-once attempt delivery until the
manager confirms execution and required delivery success.

A crash before acknowledgement SHALL leave the occurrence eligible for retry. A
crash after acknowledgement SHALL not lose successful work.

The stable occurrence identity and the session reminder key SHALL reduce
duplicate work. Netclaw SHALL not claim exactly-once delivery.

#### Scenario: The daemon stops during execution

- **GIVEN** a reminder attempt has not reached manager acknowledgement
- **WHEN** the daemon stops
- **THEN** the acknowledgement lease expires
- **AND** Akka.Reminders can retry the occurrence

#### Scenario: The daemon stops after acknowledgement

- **GIVEN** execution and required delivery succeeded
- **AND** the manager acknowledged the occurrence
- **WHEN** the daemon stops before one-shot terminal state is saved
- **THEN** durable occurrence status remains `Delivered`
- **AND** reconciliation repairs the one-shot terminal state

#### Scenario: Duplicate across snapshot recovery is accepted

- **GIVEN** a `CurrentSession` reminder was processed and `TurnRecorded` persisted
- **AND** a subsequent `SessionSnapshot` was taken
- **AND** the session later recovers from that snapshot, so journal replay skips
  events before the snapshot
- **AND** a redelivery of the original reminder arrives via Akka.Reminders
- **WHEN** the dedup pre-check runs
- **THEN** the set is empty and the redelivery is processed as a fresh turn
- **AND** this outcome is an explicit accepted tradeoff

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
fulfilled. This reuses the existing `cancel_reminder` tool (hard-delete)
rather than introducing a separate completion tool — fewer tools means
less confusion for smaller models.

#### Scenario: LLM self-cancels a fulfilled recurring reminder

- **GIVEN** an enabled interval reminder fires and the LLM executes
- **AND** the LLM determines the task is permanently fulfilled
- **WHEN** the LLM calls `cancel_reminder` with the reminder's ID
- **THEN** the reminder and its history are deleted
- **AND** future fires do not execute

#### Scenario: Prompt guidance includes reminder ID and cancellation instructions

- **GIVEN** a recurring (interval or cron) reminder definition
- **WHEN** the execution actor builds the prompt
- **THEN** the prompt includes guidance to call `cancel_reminder`
- **AND** the guidance includes the reminder's own ID

### Requirement: Delivery observation timeout alignment

The `DeliveryObservedTimeout` for Mode B (current_session) delivery MUST be aligned with the execution timeout. A delivery observation window
shorter than the execution window causes false failures when LLM turns
take longer than the observation timeout but complete before the execution
timeout.

#### Scenario: Delivery observation succeeds for LLM turns taking >30s

- **GIVEN** a Mode B reminder with `deliveryRequired = true`
- **AND** the LLM turn takes 45 seconds to produce a delivery
- **WHEN** the delivery is observed at t=45s
- **THEN** the execution completes successfully
- **AND** no failure is recorded

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

Netclaw SHALL settle each one-shot reminder exactly once.

After a successful execution, Netclaw SHALL remove the one-shot definition and its execution history.

When a one-shot reaches `FailurePauseThreshold`, Netclaw SHALL retain the definition, disable it, and record the `Failed` terminal outcome. Only an explicit delete command SHALL remove that retained definition and its history.

Below that threshold, Netclaw SHALL keep a failed one-shot enabled so Akka.Reminders can retry it.

#### Scenario: Successful one-shot is removed

- **GIVEN** a one-shot reminder succeeds
- **WHEN** Netclaw completes its acknowledgement
- **THEN** Netclaw deletes the definition and its history file
- **AND** reconciliation removes any residual `Completed` one-shot

#### Scenario: Failed one-shot remains enabled for retry

- **GIVEN** a one-shot attempt fails below the poison threshold
- **WHEN** Akka.Reminders schedules another attempt
- **THEN** Netclaw keeps the definition enabled
- **AND** reminder status shows the durable attempt state

#### Scenario: Poisoned one-shot remains inspectable

- **GIVEN** a one-shot reaches `FailurePauseThreshold`
- **WHEN** Netclaw settles the final failed attempt
- **THEN** Netclaw disables the definition with outcome `Failed`
- **AND** an all-reminders query returns the definition

#### Scenario: Reconciliation uses durable occurrence state

- **GIVEN** a one-shot has a past fire time
- **WHEN** reconciliation finds no active schedule
- **THEN** reconciliation reads the durable occurrence state
- **AND** reconciliation selects restoration, a terminal soft delete, or removal of a delivered one-shot

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

Netclaw SHALL save a successful run and reset the poison count before it sends an acknowledgement. Reconciliation SHALL repair one-shot terminal state after a post-acknowledgement process failure.

#### Scenario: Local failure state cannot be saved

- **GIVEN** an execution attempt fails
- **WHEN** Netclaw cannot save its poison state
- **THEN** Netclaw does not send a negative acknowledgement
- **AND** the Akka.Reminders acknowledgement timeout remains the recovery path

#### Scenario: Process stops after successful acknowledgement

- **GIVEN** Netclaw acknowledges a successful one-shot
- **WHEN** the process stops before it saves the terminal outcome
- **THEN** reconciliation reads the durable delivered state
- **AND** reconciliation records the completed removal
