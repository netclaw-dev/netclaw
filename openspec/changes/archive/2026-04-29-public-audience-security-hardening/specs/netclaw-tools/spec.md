## MODIFIED Requirements

### Requirement: Audience-based tool filtering

Available tools presented to the LLM SHALL be filtered per session based on
ACL policy grants and the audience profile. The Public audience profile SHALL
NOT include memory tools (`store_memory`, `find_memories`, `get_memories`,
`update_memory`). Public SHALL also default to `web_search` and `web_fetch`
disabled unless the operator explicitly allowlists them for Public. Deployment-
wide feature switches SHALL compose with this audience filtering: if a feature
runtime is disabled, its tools SHALL be absent for all audiences.

#### Scenario: Public session does not receive memory tools

- **GIVEN** a session has audience `Public`
- **WHEN** the session resolves its exposed tool set
- **THEN** `store_memory`, `find_memories`, `get_memories`, and
  `update_memory` are NOT included in the tool definitions
- **AND** `web_search` and `web_fetch` are also NOT included unless explicitly
  allowlisted for Public

#### Scenario: Team session receives memory tools

- **GIVEN** a session has audience `Team`
- **AND** `Memory.Enabled` is `true` in config
- **WHEN** the session resolves its exposed tool set
- **THEN** memory tools are included in the tool definitions

#### Scenario: Memory disabled in config removes memory tools for all audiences

- **GIVEN** `Memory.Enabled` is `false` in config
- **WHEN** a Personal-audience session resolves its exposed tool set
- **THEN** memory tools are NOT included

#### Scenario: Public search requires explicit allowlist

- **GIVEN** `Search.Enabled` is `true` in config
- **AND** a session has audience `Public`
- **AND** the Public audience profile does not include `web_search` or
  `web_fetch` in `AllowedTools`
- **WHEN** the session resolves its exposed tool set
- **THEN** `web_search` and `web_fetch` are NOT included

#### Scenario: Explicitly allowlisted Public search is exposed when runtime-enabled

- **GIVEN** `Search.Enabled` is `true` in config
- **AND** a session has audience `Public`
- **AND** the Public audience profile explicitly includes `web_search` and
  `web_fetch` in `AllowedTools`
- **WHEN** the session resolves its exposed tool set
- **THEN** `web_search` and `web_fetch` are included

#### Scenario: Search disabled in config removes search tools for all audiences

- **GIVEN** `Search.Enabled` is `false` in config
- **WHEN** a Team-audience session resolves its exposed tool set
- **THEN** `web_search` and `web_fetch` are NOT included

### Requirement: File access error message sanitization

File access denial error messages SHALL be sanitized based on the session's
`TrustAudience`. For Public audiences, error messages SHALL NOT enumerate
allowed root paths or name the session directory as an allowed root. For Team
and Personal audiences, error messages SHALL continue to include allowed root
paths for debugging.

#### Scenario: Public file access denial is sanitized

- **GIVEN** a session has audience `Public`
- **WHEN** a `file_read` tool call targets a path outside allowed roots
- **THEN** the error message does not reveal any allowed root
- **AND** no root paths are listed
- **AND** the session directory is not named or implied as an allowed root

#### Scenario: Personal file access denial is verbose

- **GIVEN** a session has audience `Personal`
- **WHEN** a `file_read` tool call targets a path outside allowed roots
- **THEN** the error message includes the list of allowed root paths

### Requirement: Public file access does not implicitly reach internal roots

The Public audience SHALL NOT implicitly inherit identity, skills, or
workspaces filesystem roots through global/default root configuration.

#### Scenario: Public cannot read identity root by default

- **GIVEN** a session has audience `Public`
- **WHEN** it attempts to read a file under the identity directory without an
  explicit Public-specific allowlist
- **THEN** the read is denied
- **AND** the denial does not reveal the internal identity path
