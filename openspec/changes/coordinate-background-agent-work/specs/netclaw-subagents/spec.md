## ADDED Requirements

### Requirement: One Main-role general worker default

The new-install profile defaults SHALL include `task-worker` for scoped code execution and complete artifacts.
The canonical release asset SHALL declare `modelRole: Main`, `timeoutSeconds: 120`, and `visibility: user-facing`.
It SHALL inherit existing provider selection and prefill timeout behavior without a provider override or new grant.
Its timeout SHALL remain an inactivity bound rather than a task lifetime budget.
The system SHALL preserve the existing specialist profiles and the default role for profiles without an explicit role.

#### Scenario: Load and route a seeded worker
- **WHEN** the init path seeds an absent worker file from the release asset
- **THEN** the runtime profile loader reads `task-worker` with role `Main`
- **AND** child creation requests the existing Main-role client

#### Scenario: Preserve specialist and default behavior
- **WHEN** the release adds the worker
- **THEN** the research, code-analysis, and summary defaults remain unchanged
- **AND** a profile without `modelRole` retains the existing Compaction default

#### Scenario: Do not claim a fixed provider
- **GIVEN** the configured Main role permits existing fallback behavior
- **WHEN** the worker requests its role client
- **THEN** that configured behavior remains authoritative
- **AND** the profile does not force a model ID or a new provider setting

### Requirement: Preserve operator profile edits and explicit updates

The init seed path SHALL create the worker only when its destination file is absent.
It SHALL preserve an existing operator worker file byte for byte.
The operator procedure SHALL use the canonical worker asset from the exact release source.
For an existing regular file, the procedure SHALL require review and explicit update authority before replacement.
The procedure SHALL stop on a directory, symbolic link, or unresolved destination conflict.
It SHALL not prescribe a new installer command or a repeat init operation as a profile update.

#### Scenario: Preserve an existing custom worker
- **GIVEN** the operator worker has custom instructions or a different role
- **WHEN** the seed path runs
- **THEN** the existing file remains unchanged and its loaded values remain authoritative

#### Scenario: Install an absent worker explicitly
- **GIVEN** an existing deployment lacks the worker and the destination is an absent ordinary path
- **WHEN** the operator reviews the exact release asset and authorizes its copy
- **THEN** the procedure installs that asset without replacement of other identity files

#### Scenario: Refuse an unresolved update target
- **WHEN** the proposed worker destination is a link, directory, or unresolved path
- **THEN** the operator procedure reports the conflict and stops
- **AND** it does not follow the target or replace the file implicitly

#### Scenario: Separate operator diagnostics from runtime skill access
- **WHEN** an operator inspects the agent-file directory for an explicit profile update
- **THEN** the procedure identifies that action as an operator diagnostic
- **AND** runtime coordination still uses logical skill access without skill-root derivation
