## ADDED Requirements

### Requirement: Project directory flows to spawned subagents as read-only context

The runtime SHALL copy the current `WorkingContext.ProjectDirectory` into the
child's immutable execution snapshot when a session spawns or routes execution
into a subagent and the directory is set. The inherited value is read-only from
the child, and subagent execution SHALL NOT mutate the parent session's
`ProjectDirectory` or other `WorkingContext` state.

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
