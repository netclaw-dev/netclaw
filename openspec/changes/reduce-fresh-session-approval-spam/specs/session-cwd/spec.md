## ADDED Requirements

### Requirement: Project work takes precedence over managed temporary work

Parent and subagent guidance SHALL use declared project scope for work that
belongs to that project. The managed temporary directory SHALL hold disposable
work outside a project. A successful project declaration SHALL not cause later
project commands to select `temp_dir` as an explicit
`WorkingDirectory`. This guidance SHALL NOT change runtime cwd resolution or
grant shell authority.

#### Scenario: Declared project remains the default for project work

- **GIVEN** `project_dir` names the project for the current task
- **WHEN** the agent runs a project shell command without a one-call override
- **THEN** guidance tells it to omit `WorkingDirectory`
- **AND** runtime cwd resolution selects `project_dir`
- **AND** guidance does not select `session_dir` for that command

#### Scenario: A named project is declared before project tool use

- **GIVEN** a task names a project that differs from `project_dir`
- **AND** `set_working_directory` is available
- **WHEN** the task needs a shell or file tool in that project
- **THEN** guidance tells the agent to declare the project once
- **AND** the declaration precedes the first shell or file tool call
- **AND** the declaration does not grant shell authority

#### Scenario: A rejected named path is corrected before project work

- **GIVEN** a task names a project path and a fallback project path
- **AND** `set_working_directory` is available
- **WHEN** the first declaration is rejected
- **THEN** guidance declares the fallback before another project tool
- **AND** the first declaration uses the task's first project path exactly
- **AND** guidance does not substitute an enclosing directory before rejection
- **AND** guidance does not probe either path before its declaration
- **AND** neither declaration grants shell authority

#### Scenario: Child declaration governs later child project commands

- **GIVEN** a subagent successfully declares a different user-named project
- **WHEN** it runs later shell commands for that project
- **THEN** guidance uses the child `project_dir`
- **AND** it does not pass the parent `temp_dir` as `WorkingDirectory`
- **AND** it does not add an inline directory change to reach the project

#### Scenario: One call in a child directory uses typed scope

- **GIVEN** `project_dir` names a project root
- **AND** one shell call must run in a named child directory or worktree
- **WHEN** the agent authors that call
- **THEN** `Command` contains only the shell operation
- **AND** `WorkingDirectory` contains the exact child directory
- **AND** the persistent project root does not change

#### Scenario: Disposable work outside a project uses managed temporary storage

- **GIVEN** a task creates disposable artifacts that do not belong to a project
- **AND** the audience can see its managed temporary directory
- **AND** the task does not require a platform temporary path
- **WHEN** the agent selects a working directory
- **THEN** guidance selects `temp_dir`
- **AND** writable artifacts do not use platform temporary storage
- **AND** it does not declare the managed temporary directory as a project

#### Scenario: Requested directory transition remains authored behavior

- **GIVEN** the task explicitly asks to test or perform a shell directory transition
- **WHEN** the agent authors the shell call
- **THEN** guidance preserves the inline transition
- **AND** the call follows ordinary approval policy

#### Scenario: Project declaration does not authorize project commands

- **GIVEN** an agent successfully declares a project
- **WHEN** it authors a prompt-worthy command in that project
- **THEN** the declaration supplies only a trusted root
- **AND** the command still needs reviewed-safe, one-time, session, or stored authority

### Requirement: Parent and child contexts share one directory-selection order

Personal and Team parent and subagent contexts SHALL state the same directory
selection order. Project work SHALL use `project_dir`. A named one-call child
scope SHALL use typed `WorkingDirectory`. Disposable non-project work SHALL use
`temp_dir`. An inline directory change SHALL remain only for requested
directory behavior. Public context SHALL not reveal a private project or
session path.

#### Scenario: Parent context contains the complete order

- **GIVEN** a Personal or Team parent context has project and session paths
- **WHEN** the context is assembled
- **THEN** it distinguishes project work from managed temporary work
- **AND** it distinguishes one-call typed scope from persistent project scope

#### Scenario: Child context contains the complete order

- **GIVEN** a Personal or Team child receives project and session context
- **WHEN** its first model message is assembled
- **THEN** it receives the same directory-selection order as the parent
- **AND** the session rule does not use an unconditional temporary-directory instruction

#### Scenario: Project refresh does not duplicate temporary guidance

- **GIVEN** a child context already contains one managed-temporary-directory rule
- **WHEN** `set_working_directory` refreshes child project context
- **THEN** the next prompt contains the updated `project_dir`
- **AND** it contains exactly one managed-temporary-directory rule
- **AND** the directory-selection order remains unchanged

#### Scenario: Public context stays redacted

- **GIVEN** a Public parent or subagent context
- **WHEN** the context is assembled
- **THEN** no private `project_dir` or `session_dir` value is disclosed
- **AND** no unavailable scope tool is recommended

#### Scenario: Failed declaration preserves prior scope

- **GIVEN** `set_working_directory` rejects a requested path
- **WHEN** the next model call starts
- **THEN** the prior project scope remains unchanged
- **AND** guidance does not treat the rejected path as declared
- **AND** an authored shell call follows ordinary approval policy
