## ADDED Requirements

### Requirement: Project directory flows to spawned subagents as read-only context

When a session spawns or routes execution into a subagent, the current
`WorkingContext.ProjectDirectory` SHALL be copied into the child's immutable
execution snapshot when it is set.

This inherited value is read-only from the child. Subagent execution SHALL NOT
mutate the parent session's `ProjectDirectory` or other `WorkingContext` state.

#### Scenario: Subagent inherits current project directory

- **GIVEN** a session has `WorkingContext.ProjectDirectory` set to
  `/home/user/workspaces/netclaw`
- **WHEN** the session starts a subagent run
- **THEN** the child execution snapshot contains
  `/home/user/workspaces/netclaw`

#### Scenario: Subagent does not change parent project directory

- **GIVEN** a session has `WorkingContext.ProjectDirectory` set to
  `/home/user/workspaces/netclaw`
- **WHEN** a spawned subagent completes
- **THEN** the parent session still has the same `ProjectDirectory`
- **AND** no child-side action implicitly rewrites the parent working context
