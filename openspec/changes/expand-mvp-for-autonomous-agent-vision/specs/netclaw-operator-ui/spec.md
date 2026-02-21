## ADDED Requirements

### Requirement: Phase 5 deferral

The operator UI implementation SHALL be deferred to Phase 5. The MVP
deliverable for the operator UI is a specification and mockup only. No
runtime UI code SHALL be included in the MVP build.

#### Scenario: MVP excludes UI runtime code

- **WHEN** the MVP is built
- **THEN** no operator UI runtime code is included in the build artifacts
- **AND** the UI specification and mockups exist as planning documents

#### Scenario: UI specs maintained for Phase 5

- **GIVEN** the operator UI is deferred to Phase 5
- **WHEN** UI-related capability specs are updated
- **THEN** the specs define future behavior for Phase 5 implementation

### Requirement: Memory and configuration screen

The operator UI SHALL provide a screen for viewing personality files, the
project registry, and the environment inventory. This screen provides
read-only visibility into the agent's local memory and configuration state.

#### Scenario: View personality files

- **WHEN** an operator opens the memory and configuration screen
- **THEN** the contents of PERSONALITY.md, INSTRUCTIONS.md, and USER.md are
  displayed

#### Scenario: View project registry

- **WHEN** an operator opens the memory and configuration screen
- **THEN** the project registry is displayed with project names, paths, and
  capabilities

#### Scenario: View environment inventory

- **WHEN** an operator opens the memory and configuration screen
- **THEN** the environment inventory is displayed with installed tools,
  credential status, and MCP server reachability

### Requirement: Scheduling screen

The operator UI SHALL provide a screen for viewing, creating, pausing, and
deleting scheduled tasks. The screen SHALL also display execution history for
each task.

#### Scenario: View scheduled tasks

- **WHEN** an operator opens the scheduling screen
- **THEN** all scheduled tasks are listed with name, schedule, status, and
  last execution result

#### Scenario: Create scheduled task from UI

- **WHEN** an operator creates a new scheduled task from the scheduling screen
- **THEN** the task is added to the schedule registry with the specified
  schedule, instructions, and required tool grants

#### Scenario: Pause and delete from UI

- **GIVEN** a scheduled task exists
- **WHEN** an operator pauses or deletes the task from the scheduling screen
- **THEN** the task status is updated accordingly

#### Scenario: View execution history

- **WHEN** an operator selects a scheduled task on the scheduling screen
- **THEN** the execution history is displayed with timestamps, outcomes, and
  error details for failed runs
