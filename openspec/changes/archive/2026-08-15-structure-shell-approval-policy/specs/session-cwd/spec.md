## ADDED Requirements

### Requirement: Project-directory declarations reject control characters

The `set_working_directory` tool SHALL reject a path that contains NUL, CR, or
LF before filesystem resolution. The tool SHALL return a bounded error without
echoing the authored path.

#### Scenario: Controlled path cannot become project scope

- **GIVEN** a path contains NUL, CR, or LF
- **WHEN** an agent calls `set_working_directory` with that path
- **THEN** the tool returns an error without the authored path
- **AND** the project scope remains unchanged
- **AND** project instructions are not loaded from that path
