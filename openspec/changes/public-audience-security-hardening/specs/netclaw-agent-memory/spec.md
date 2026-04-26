## MODIFIED Requirements

### Requirement: Memory gated by audience and config

Cross-session memory (recall, extraction, distillation, and tool access)
SHALL be gated by both the session's `TrustAudience` and the
`MemoryConfig.Enabled` flag. When either gate denies access, memory
operations SHALL be fully suppressed: no reads, no writes, no recall.

Public-audience sessions SHALL have memory fully disabled regardless of
config. This eliminates the memory taint vector where hostile Public users
inject false facts that later surface in privileged sessions.

When Public memory is disabled, existing Public-authored memories SHALL become
inert for normal recall/search going forward. This change does not require any
automatic purge, migration, or cleanup feature. Deliberate purge by a higher-
privilege session may be implemented later.

#### Scenario: Public session has no automatic recall

- **GIVEN** a session has audience `Public`
- **WHEN** the session resolves automatic recall for a new turn
- **THEN** `SessionRecallManager.ResolveForTurn()` returns an empty
  result immediately
- **AND** no memory search is performed

#### Scenario: Public session skips memory extraction

- **GIVEN** a session has audience `Public`
- **WHEN** the distillation pipeline produces memory proposals
- **THEN** the memory proposal gate is NOT evaluated
- **AND** no memory operations are sent to the curation actor

#### Scenario: Config-disabled memory suppresses recall for Team

- **GIVEN** `Memory.Enabled` is `false` in config
- **AND** a session has audience `Team`
- **WHEN** the session resolves automatic recall
- **THEN** automatic recall returns an empty result

#### Scenario: Config-enabled memory with Team audience works normally

- **GIVEN** `Memory.Enabled` is `true` in config
- **AND** a session has audience `Team`
- **WHEN** the session resolves automatic recall
- **THEN** automatic recall executes normally with audience-scoped filtering

#### Scenario: Legacy Public memories are inert for normal recall/search

- **GIVEN** memory storage still contains historical items authored under
  audience `Public`
- **AND** Public memory has been disabled by this change's gating rules
- **WHEN** a normal recall/search path executes
- **THEN** those historical Public items are not surfaced in recall/search
- **AND** no purge operation is required for this behavior

### Requirement: Self-configuration through conversation

The system SHALL allow the agent to modify identity files (`SOUL.md`,
`TOOLING.md`) and skill files (`~/.netclaw/skills/*.md`) through
conversation using `file_read` and `file_write`. The `netclaw-identity`
built-in skill SHALL provide triage guidance for what information goes where.
The agent SHALL NOT have tools that directly modify `netclaw.json`,
`secrets.json`, ACL, or security policy. **AGENTS.md SHALL NOT be modifiable
through conversation**: it is binary-controlled firmware.

#### Scenario: Agent updates SOUL.md

- **GIVEN** the user asks the agent to adjust its personality
- **WHEN** the agent uses `file_write` to update `SOUL.md`
- **THEN** the changes are persisted and reflected in future sessions

#### Scenario: Agent cannot modify AGENTS.md runtime behavior

- **GIVEN** the user asks the agent to change its operating rules
- **WHEN** the agent attempts to write to `AGENTS.md`
- **THEN** runtime behavior continues to use the embedded resource
- **AND** no Public session file-root default exposes the binary-controlled
  AGENTS source through implicit file access
