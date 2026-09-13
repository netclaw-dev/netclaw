## ADDED Requirements

### Requirement: Subagent context announces private session scratch

Before the first model call, the system SHALL include the exact bound `session_dir` in Personal and Team subagent working context and SHALL identify it as private scratch for disposable artifacts. The guidance SHALL preserve an explicitly required platform temporary path. Public subagent context SHALL NOT include the private session path.

The context SHALL be derived from the child run's existing bound session scope. It SHALL NOT add a public protocol field, persist the path as agent identity, create a second scratch directory, or change shell authorization.

#### Scenario: Personal child receives exact scratch path

- **GIVEN** a Personal subagent has bound session directory `/home/user/.netclaw/sessions/example`
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context contains `session_dir: /home/user/.netclaw/sessions/example`
- **AND** it identifies `session_dir` as the location for disposable artifacts
- **AND** it does not imply that the directory grants shell authority

#### Scenario: Team child receives exact scratch path

- **GIVEN** a Team subagent has a valid bound session directory
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context contains that exact directory as private scratch
- **AND** existing Team tool and shell policy remains unchanged

#### Scenario: Public child retains path redaction

- **GIVEN** a Public subagent has an internal bound session directory
- **WHEN** Netclaw assembles its initial model context
- **THEN** the context does not contain that directory
- **AND** no scratch guidance discloses another private filesystem path

#### Scenario: Explicit platform temporary requirement is preserved

- **GIVEN** a Personal or Team subagent receives scratch guidance
- **WHEN** its task explicitly requires `/tmp` or the native Windows temporary directory
- **THEN** the guidance tells the child to preserve that requirement
- **AND** Netclaw does not rewrite the path or grant authority to it

#### Scenario: Project declaration does not replace session scratch

- **GIVEN** a child has received its initial session scratch context
- **WHEN** it later calls `set_working_directory` successfully
- **THEN** its project scope and project instructions update through the existing contract
- **AND** its bound `session_dir` remains unchanged
