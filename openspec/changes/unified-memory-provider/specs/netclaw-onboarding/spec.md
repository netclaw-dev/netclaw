## MODIFIED Requirements

### Requirement: Guided onboarding

The CLI SHALL provide guided setup through `netclaw init`. The wizard SHALL
include a memory configuration step that asks the operator whether to use
file-based memory (default) or Memorizer, and configures accordingly.

#### Scenario: Memory step offers provider choice

- **WHEN** onboarding reaches the memory configuration step
- **THEN** the wizard presents options: "File-based memory (default)" and
  "Memorizer (MCP server)"
- **AND** file-based is pre-selected as the recommended option

#### Scenario: File-based memory selected

- **GIVEN** the operator selects file-based memory
- **WHEN** the wizard writes configuration
- **THEN** `netclaw.json` includes `"Memory": { "Provider": "files" }`
- **AND** `~/.netclaw/memories/` directory is created
- **AND** no MCP server configuration is added for Memorizer

#### Scenario: Memorizer selected

- **GIVEN** the operator selects Memorizer
- **WHEN** the wizard advances to Memorizer configuration
- **THEN** the wizard prompts for the Memorizer endpoint URL
  (default: `http://localhost:5012/mcp`)
- **AND** `netclaw.json` includes `"Memory": { "Provider": "memorizer" }`
- **AND** the Memorizer MCP server entry is added to `McpServers`

#### Scenario: Health check validates memory provider

- **WHEN** the wizard runs the final health check
- **THEN** the health check validates the configured memory provider
- **AND** for file-based: verifies directory exists and is writable
- **AND** for Memorizer: verifies MCP server connectivity
