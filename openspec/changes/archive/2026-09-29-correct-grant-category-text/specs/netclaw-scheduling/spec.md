## MODIFIED Requirements

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
