## MODIFIED Requirements

### Requirement: Diagnostic commands

The `netclaw doctor` command SHALL include a memory provider health check.
The `netclaw status` command SHALL display the active memory provider and
its health status.

#### Scenario: Doctor validates file-based memory

- **GIVEN** the memory provider is `files`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor checks that `~/.netclaw/memories/` exists and is writable
- **AND** reports pass/fail for the memory provider check

#### Scenario: Doctor validates Memorizer

- **GIVEN** the memory provider is `memorizer`
- **WHEN** the operator runs `netclaw doctor`
- **THEN** doctor checks that the Memorizer MCP server is configured
- **AND** checks that the MCP server is reachable
- **AND** reports pass/fail with remediation guidance if unhealthy

#### Scenario: Status shows memory provider

- **WHEN** the operator runs `netclaw status`
- **THEN** the output includes a `memory:` line showing:
  - Provider name (`files` or `memorizer`)
  - Health status (`healthy`, `degraded`, or `unavailable`)
  - For Memorizer: endpoint URL and tool count
  - For files: memory count and index path
