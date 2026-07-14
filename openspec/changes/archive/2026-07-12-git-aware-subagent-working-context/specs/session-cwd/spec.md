## ADDED Requirements

### Requirement: Working context includes derived Git worktree state
For Team and Personal turns whose `WorkingContext.ProjectDirectory` is inside a Git worktree, the system SHALL derive a fresh Git snapshot at turn start and render it as a nested section of `[working-context]`. The snapshot SHALL include worktree root, common repository directory, branch or detached state, HEAD, upstream and ahead/behind when configured, and staged, modified, and untracked counts. Derived Git state SHALL NOT be persisted in session state.

#### Scenario: Linked worktree is distinguished from common repository
- **GIVEN** a session project directory inside a linked Git worktree
- **WHEN** the next turn-start working-context snapshot is built
- **THEN** the model-visible context identifies the linked worktree path and common repository directory
- **AND** reports the linked worktree's branch and HEAD

#### Scenario: Git state refreshes on the next turn
- **GIVEN** a tool changes branch, HEAD, or dirty state during one turn
- **WHEN** the session begins its next turn
- **THEN** the new volatile working-context nudge contains the updated Git snapshot
- **AND** earlier history messages are not rewritten

#### Scenario: Non-Git project has no Git section
- **GIVEN** a valid project directory that is not inside a Git worktree
- **WHEN** working context is assembled
- **THEN** the normal project and recent-file context remains available
- **AND** no Git section is rendered

#### Scenario: Git inspection failure is visible
- **GIVEN** a project directory whose Git state cannot be inspected because Git is missing, times out, or the repository is invalid
- **WHEN** working context is assembled for an eligible audience
- **THEN** Git status is reported as unavailable with a sanitized reason
- **AND** the failure is not represented as a clean or non-Git worktree

#### Scenario: Git remote credentials are never rendered
- **GIVEN** a repository with a credential-bearing remote URL
- **WHEN** Git working context is rendered
- **THEN** no remote credentials or complete remote URL appears in model-visible context or logs
