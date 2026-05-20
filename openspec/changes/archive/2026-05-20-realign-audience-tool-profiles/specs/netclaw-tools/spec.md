## ADDED Requirements

### Requirement: Directory enumeration tool

The system SHALL provide a `file_list` first-party tool that returns a
single-level listing of a directory's entries, each entry identified by name
and type (file or directory). `file_list` SHALL be read-only and SHALL NOT
create, modify, or remove any filesystem entry.

`file_list` SHALL be a profile-managed tool gated by the audience profile
`AllowedTools` allowlist. Its target directory SHALL be authorized through the
same scoped read-access policy used by `file_read`, so the directories an
audience may list are exactly that audience's resolved read roots. A target
outside the audience's read roots SHALL be denied, and the denial message
SHALL NOT disclose configured root paths.

#### Scenario: Team session lists a directory within its read roots

- **GIVEN** a session resolved to the `Team` audience with `file_list` granted
- **WHEN** the agent invokes `file_list` on its session directory
- **THEN** the tool returns the directory's entries with name and type
- **AND** no filesystem entry is created, modified, or removed

#### Scenario: Public session cannot list outside its session directory

- **GIVEN** a session resolved to the `Public` audience
- **WHEN** the agent invokes `file_list` on a path outside the session
  directory
- **THEN** the invocation is denied
- **AND** the denial message does not disclose configured root paths

#### Scenario: file_list denied when not granted to the audience

- **GIVEN** an audience profile whose `AllowedTools` omits `file_list`
- **WHEN** the agent invokes `file_list`
- **THEN** the invocation is denied with reason
  `tool_not_allowed_for_audience_profile`
