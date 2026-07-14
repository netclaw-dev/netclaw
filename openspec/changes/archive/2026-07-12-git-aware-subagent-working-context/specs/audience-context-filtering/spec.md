## MODIFIED Requirements

### Requirement: Working context suppression for Public

The working context block, including project directory, recent files, Git worktree paths, branch, HEAD, and dirty state, SHALL NOT be injected into Public-audience main sessions or subagents.

#### Scenario: Public session has no working context

- **WHEN** a Public-audience session has a non-empty working context or eligible Git project directory
- **THEN** no `[working-context]` block is injected into the volatile context block
- **AND** no Git inspection result is exposed to the model

#### Scenario: Public subagent receives no internal working context

- **GIVEN** a subagent is launched under a Public parent turn
- **WHEN** the child initial prompt is assembled
- **THEN** no parent project path, recent-file list, or Git state is included

#### Scenario: Team session receives working context

- **WHEN** a Team-audience session has a non-empty working context
- **THEN** `WorkingContext` and any successfully derived Git enrichment are injected into the volatile context block
