## MODIFIED Requirements

### Requirement: Working context includes derived Git worktree state
For Team and Personal turns whose `WorkingContext.ProjectDirectory` is declared and Git identifies it as a worktree, the system SHALL asynchronously derive a fresh Git snapshot at turn start and render it as a nested section of `[working-context]`. The snapshot SHALL include worktree root, common repository directory, branch or detached state, HEAD, upstream and ahead/behind when configured, and staged, modified, and untracked counts. Derived Git state SHALL NOT be persisted in session state. Git inspection SHALL return explicit available, not-repository, executable-not-found, or unavailable outcomes.

#### Scenario: Linked worktree is distinguished from common repository
- **GIVEN** a Team or Personal session project directory inside a linked Git worktree
- **WHEN** the next turn-start working-context snapshot is built
- **THEN** the model-visible context identifies the linked worktree path and common repository directory
- **AND** reports the linked worktree's branch and HEAD

#### Scenario: Git state refreshes on the next turn
- **GIVEN** a tool changes branch, HEAD, or dirty state during one turn
- **WHEN** the session begins its next turn
- **THEN** the new volatile working-context nudge contains the updated Git snapshot
- **AND** earlier history messages are not rewritten

#### Scenario: Non-Git project has no Git section
- **GIVEN** a declared project directory that Git identifies as not a repository
- **WHEN** working context is assembled
- **THEN** normal project and recent-file context remains available
- **AND** no Git section is rendered

#### Scenario: Git inspection failure is visible
- **GIVEN** an eligible project directory whose Git state cannot be inspected because Git is missing, times out, or the repository is invalid
- **WHEN** working context is assembled
- **THEN** Git status is reported as unavailable with a sanitized reason
- **AND** the failure is not represented as a clean or non-Git worktree

#### Scenario: Stale Git result is discarded
- **GIVEN** asynchronous Git inspection began for an earlier turn generation
- **WHEN** its result arrives after the session has advanced to another turn
- **THEN** the actor discards the stale result
- **AND** it is not rendered into the active turn

#### Scenario: Git remote credentials are never rendered
- **GIVEN** a repository with a credential-bearing remote URL
- **WHEN** Git working context is rendered
- **THEN** no remote credentials or complete remote URL appears in model-visible context or logs
