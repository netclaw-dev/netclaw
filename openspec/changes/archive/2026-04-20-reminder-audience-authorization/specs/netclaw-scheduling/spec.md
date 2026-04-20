## MODIFIED Requirements

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

### Requirement: Isolated task execution

Each scheduled task execution SHALL run in a fresh session actor with its own
context. The session SHALL load the agent personality and any relevant project
context overlays. Scheduled sessions SHALL NOT share state with interactive
sessions.

Execution MAY trust the stored reminder audience because reminder minting and
import paths SHALL validate the persisted audience before the definition is
saved.

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

#### Scenario: Execution uses validated stored audience

- **GIVEN** a reminder definition was accepted with stored audience `Public`
- **WHEN** the reminder later executes on schedule
- **THEN** the execution session uses stored audience `Public`
- **AND** it does not recompute audience from the deployment posture default
