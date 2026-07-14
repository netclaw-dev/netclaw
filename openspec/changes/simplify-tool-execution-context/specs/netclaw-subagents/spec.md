## MODIFIED Requirements

### Requirement: Subagents maintain run-scoped working context
Each subagent SHALL own an ephemeral working context initialized by forking a read-only snapshot of the parent session's project directory, recent files, and immutable admitted-turn authority. The child SHALL own fresh call-local activity tracking and SHALL evolve its working state independently. The initial snapshot SHALL be included in the runtime-context portion of the child user message and SHALL NOT modify the reusable subagent system prompt. Child activity SHALL NOT mutate parent session state during execution.

#### Scenario: Child receives parent recent-file grounding
- **GIVEN** a parent session with a project directory and recent files
- **WHEN** it spawns a permitted subagent
- **THEN** the child's initial model input contains the parent project directory and recent-file snapshot
- **AND** its tool execution uses the explicitly inherited admitted-turn authority

#### Scenario: Child file activity is isolated
- **GIVEN** a running child that reads or changes a file
- **WHEN** the child updates its run-scoped working context
- **THEN** the parent durable working context is unchanged until a successful child completion delta is handled
- **AND** another child cannot observe that call-local activity through shared mutable state

### Requirement: Subagent completion returns structured working context
`SubAgentResult` SHALL carry a typed child outcome and structured working-context delta containing project/worktree identity, files read, confirmed files changed through recognized first-party file tools, files observed changed between bounded Git snapshots, and final branch and HEAD when available. Observed worktree changes SHALL NOT be represented as exclusively authored by the child. Failed or cancelled outcomes SHALL carry no mergeable delta.

#### Scenario: First-party edit is confirmed
- **GIVEN** a child changes a file through a recognized first-party file tool
- **WHEN** the child completes successfully
- **THEN** the canonical path appears in confirmed changed files

#### Scenario: Shell-generated file is observed
- **GIVEN** a child invokes a shell command that changes a Git worktree file without first-party file-tool provenance
- **WHEN** final Git state differs from the spawn snapshot
- **THEN** the file appears in observed changed files
- **AND** is not claimed as a confirmed child-authored file

#### Scenario: Parent merges only confirmed successful activity
- **GIVEN** a child completes successfully with confirmed and observed file metadata
- **WHEN** the parent handles the structured result
- **THEN** confirmed files are merged into the parent's durable recent-file context
- **AND** observed-only files are not silently merged or attributed

#### Scenario: Failed child does not merge partial activity
- **GIVEN** a child fails or is cancelled after touching files
- **WHEN** the parent handles the failure result
- **THEN** the outcome contains no mergeable working-context delta
- **AND** no child file metadata is merged into parent durable working context
