## ADDED Requirements

### Requirement: MCP prompt discovery and generation ownership

The system SHALL list prompts when an enabled MCP server declares prompt support.
It SHALL publish prompt descriptors in the same immutable server generation as the discovered tools.

Each descriptor SHALL use the logical name `mcp__<server>__<prompt>`.
It SHALL retain the server name, prompt name, prompt arguments, and generation.

#### Scenario: Prompt-capable server connects

- **GIVEN** an enabled server declares prompt support
- **WHEN** the daemon initializes the server connection
- **THEN** the daemon lists the server prompts
- **AND** it publishes the tools and prompts in one server generation
- **AND** each prompt appears in the skill registry under its canonical logical name

#### Scenario: Tool-only server connects

- **GIVEN** an enabled server does not declare prompt support
- **WHEN** the daemon initializes the server connection
- **THEN** the daemon does not call `prompts/list`
- **AND** the server tools remain available

#### Scenario: Prompt discovery fails during replacement

- **GIVEN** a healthy published server generation
- **WHEN** a replacement candidate cannot list its declared prompts
- **THEN** the system keeps the prior server generation
- **AND** it keeps the prior MCP prompt skill inventory
- **AND** diagnostics report the replacement failure

### Requirement: MCP prompt catalog poll

The existing MCP catalog poll SHALL include prompts for a prompt-capable server.
It SHALL publish one replacement generation when a tool or prompt descriptor changes.

#### Scenario: Prompt descriptor changes

- **GIVEN** a connected server changes a prompt description or argument descriptor
- **WHEN** the next catalog poll succeeds
- **THEN** the system publishes a new server generation
- **AND** the skill registry contains the new prompt descriptor

#### Scenario: Prompt catalog becomes empty

- **GIVEN** a connected prompt-capable server removes its final prompt
- **WHEN** the next catalog poll succeeds with an empty prompt list
- **THEN** the system removes that server's MCP prompt skills
- **AND** it preserves the server's tools and file skills

### Requirement: MCP prompt server permission

The system SHALL use the existing MCP server grant for prompt discovery and prompt use.
It SHALL NOT add a prompt-specific grant category.

#### Scenario: Audience can use the server

- **GIVEN** an audience can use MCP server `gigatron`
- **WHEN** the system builds that audience's skill index
- **THEN** allowed `mcp__gigatron__*` prompt skills appear

#### Scenario: Audience cannot use the server

- **GIVEN** an audience cannot use MCP server `gigatron`
- **WHEN** the system builds that audience's skill index or handles a prompt load
- **THEN** no `gigatron` prompt descriptor appears
- **AND** the load follows the generic denied result

#### Scenario: Unknown skill fallback does not reveal remote prompts

- **GIVEN** the registry contains MCP prompt skills from one or more servers
- **WHEN** a session requests an unknown skill name
- **THEN** the fallback list contains no MCP server or prompt names
- **AND** the audience-filtered skill index remains the discovery source for remote prompts

### Requirement: MCP prompt load generation and failure behavior

The system SHALL resolve an MCP prompt through the client generation that supplied its skill descriptor.
It SHALL fail visibly when the descriptor is stale, the server is unavailable, or the result has unsupported content.

#### Scenario: Current prompt descriptor loads

- **GIVEN** an MCP prompt skill references the current server generation
- **WHEN** `skill_load` loads the prompt
- **THEN** the system calls `prompts/get` on that generation
- **AND** the result identifies the source server, prompt, and generation
- **AND** the result preserves each prompt message role

#### Scenario: Stale prompt descriptor fails

- **GIVEN** an MCP prompt skill references a replaced server generation
- **WHEN** `skill_load` loads the prompt
- **THEN** the system returns an explicit stale-generation error
- **AND** it does not call `prompts/get` on the new generation

#### Scenario: Unsupported prompt content fails

- **GIVEN** `prompts/get` returns a content block that this slice cannot render
- **WHEN** the adapter processes the result
- **THEN** it returns an explicit unsupported-content error
- **AND** it does not silently omit the block
