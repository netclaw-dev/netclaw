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

### Requirement: Resolved shell cwd flows to spawned subagents as read-only snapshot

When a session spawns a subagent, the runtime SHALL capture the parent's
*resolved* shell working directory at spawn time — equivalent to the value
`ToolExecutionContext.ResolveShellCwd(null)` returns on the parent's tool
execution context — and populate the child's
`ToolExecutionContext.InheritedCwd` with that value before any tool
authorization runs inside the child. `ToolExecutionContext.Cwd` remains the
per-call resolved output written by the approval gate when a concrete tool
invocation is evaluated. The inherited cwd SHALL be read-only from the child:
subagent execution SHALL NOT mutate the parent session's
`WorkingContext.ProjectDirectory` or otherwise rewrite the parent's own cwd
inputs. When the parent has no resolvable cwd at spawn time (no explicit
working directory, no `ProjectDirectory`, no `SessionDirectory`), the child's
`InheritedCwd` SHALL be `null`; the approval gate SHALL continue to evaluate
persisted global grants in that case as defined by the
`tool-approval-gates` capability.

#### Scenario: Subagent inherits parent's resolved working directory

- **GIVEN** a session whose parent `ToolExecutionContext.ResolveShellCwd(null)` resolves to
  `/home/user/repos/foo`
- **WHEN** the session spawns a subagent
- **THEN** the subagent's `ToolExecutionContext.InheritedCwd` is
  `/home/user/repos/foo` before its first tool invocation
- **AND** a `shell_execute` call inside the subagent with no
  `WorkingDirectory` argument resolves cwd to `/home/user/repos/foo` for
  approval purposes

#### Scenario: Subagent shell approval shows the inherited cwd in the header

- **GIVEN** the parent's resolved cwd at spawn time is `/home/user/repos/foo`
- **WHEN** the subagent invokes an approval-gated shell command with no
  explicit `WorkingDirectory` argument
- **THEN** the approval prompt header reads
  `Approve <verb> in /home/user/repos/foo?`
- **AND** the prompt header does NOT read
  `Approve <verb> in (no working directory)?`

#### Scenario: Subagent with no inheritable cwd surfaces null cwd faithfully

- **GIVEN** the parent's `ToolExecutionContext.ResolveShellCwd(null)` returns
  `null` (no explicit cwd, no `ProjectDirectory`, no `SessionDirectory`)
- **WHEN** the session spawns a subagent
- **THEN** the subagent's `ToolExecutionContext.InheritedCwd` is `null`
- **AND** the approval gate SHALL still evaluate persisted global grants
  per the `tool-approval-gates` capability

#### Scenario: Subagent does not mutate parent's working context

- **GIVEN** a session with `WorkingContext.ProjectDirectory` =
  `/home/user/repos/foo` and resolved cwd = `/home/user/repos/foo`
- **WHEN** a spawned subagent runs to completion (succeeds, fails, or times
  out)
- **THEN** the parent session's `WorkingContext.ProjectDirectory` is unchanged
- **AND** no subagent action implicitly rewrites the parent working context
