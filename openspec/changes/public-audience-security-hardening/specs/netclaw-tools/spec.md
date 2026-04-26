## MODIFIED Requirements

### Requirement: Audience-based tool filtering

Available tools presented to the LLM SHALL be filtered per session based on
ACL policy grants and the audience profile. The Public audience profile SHALL
NOT include memory tools (`store_memory`, `find_memories`, `get_memories`,
`update_memory`).

#### Scenario: Public session does not receive memory tools

- **GIVEN** a session has audience `Public`
- **WHEN** the session resolves its exposed tool set
- **THEN** `store_memory`, `find_memories`, `get_memories`, and
  `update_memory` are NOT included in the tool definitions
- **AND** other allowed tools (file_read, file_write, attach_file,
  web_search, web_fetch) remain available per the Public profile

#### Scenario: Team session receives memory tools

- **GIVEN** a session has audience `Team`
- **AND** `Memory.Enabled` is `true` in config
- **WHEN** the session resolves its exposed tool set
- **THEN** memory tools are included in the tool definitions

#### Scenario: Memory disabled in config removes memory tools for all audiences

- **GIVEN** `Memory.Enabled` is `false` in config
- **WHEN** a Personal-audience session resolves its exposed tool set
- **THEN** memory tools are NOT included

### Requirement: File access error message sanitization

File access denial error messages SHALL be sanitized based on the session's
`TrustAudience`. For Public audiences, error messages SHALL NOT enumerate
allowed root paths. For Team and Personal audiences, error messages SHALL
continue to include allowed root paths for debugging.

#### Scenario: Public file access denial is sanitized

- **GIVEN** a session has audience `Public`
- **WHEN** a `file_read` tool call targets a path outside allowed roots
- **THEN** the error message is:
  `"Error: Public trust context may only access files inside the current session directory."`
- **AND** no root paths are listed

#### Scenario: Personal file access denial is verbose

- **GIVEN** a session has audience `Personal`
- **WHEN** a `file_read` tool call targets a path outside allowed roots
- **THEN** the error message includes the list of allowed root paths
