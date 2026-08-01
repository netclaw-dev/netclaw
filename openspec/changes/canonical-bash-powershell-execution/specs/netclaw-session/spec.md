## ADDED Requirements

### Requirement: Execution environment in working context

For every shell-capable non-Public turn, the session SHALL include the canonical execution environment in the existing `[working-context]` volatile context block. The block SHALL report operating-system family, shell executable, preferred grammar, and path style from the same immutable environment used by execution and security parsing. Public turns SHALL not disclose the execution environment while Public lacks shell access.

#### Scenario: Model receives canonical shell facts

- **GIVEN** a Personal or Team turn can use shell execution
- **WHEN** the working-context snapshot is rendered
- **THEN** it contains the canonical platform, executable, grammar, and path style
- **AND** those values match the shell and parser used for the tool call

#### Scenario: Public context omits shell environment

- **GIVEN** a Public turn has no shell grant
- **WHEN** the working-context snapshot is rendered
- **THEN** it does not disclose the host execution environment

### Requirement: Execution context preserves prompt-prefix caching

The execution-environment subsection SHALL use the existing persisted volatile-context nudge path. Adding a later turn or observing changed Git state SHALL append new history without rewriting prior system or working-context bytes. The environment SHALL remain available after compaction and in child-run grounding.

#### Scenario: Two-turn prefix remains byte stable

- **GIVEN** a session completes one model turn with an execution-environment subsection
- **WHEN** a second turn observes different Git state
- **THEN** the first turn's system and volatile-context bytes are unchanged
- **AND** the second turn appends its context after the cached prefix

#### Scenario: Environment survives compaction and child fork

- **GIVEN** a session with a canonical execution environment compacts or spawns a child run
- **WHEN** the next model call or child call is assembled
- **THEN** the applicable working context still identifies the same execution environment
