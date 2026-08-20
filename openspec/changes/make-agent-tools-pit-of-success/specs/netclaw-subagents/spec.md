## ADDED Requirements

### Requirement: Subagents use progressive tool disclosure

A subagent SHALL begin with the same policy-exposed core tool set as a main
session, minus tools prohibited by subagent policy. It SHALL NOT eagerly receive
every discoverable first-party or MCP tool. `search_tools` and `load_tool` SHALL
activate deferred tools only in that child actor's ephemeral exposure set.

#### Scenario: Child starts with core rather than full catalog

- **GIVEN** the daemon has more than one hundred policy-visible specialty and
  MCP tools
- **WHEN** a subagent starts
- **THEN** its first model request contains only its allowed core tools
- **AND** `search_tools` can find allowed deferred capabilities

#### Scenario: Child loads one deferred tool

- **GIVEN** a subagent needs a policy-visible deferred tool
- **WHEN** it searches for and loads that exact tool
- **THEN** the next child model request contains the core plus that tool
- **AND** unrelated deferred schemas remain absent

#### Scenario: Child cannot discover recursive delegation

- **GIVEN** `spawn_agent` is registered for the parent session
- **WHEN** a subagent searches for or attempts to load it
- **THEN** the response does not reveal or activate `spawn_agent`

### Requirement: Subagent tool exposure is observable without payloads

Subagent diagnostics SHALL report core, deferred-visible, and dynamically loaded
tool counts for each run. Exposure diagnostics SHALL NOT include tool argument
values, command text, file paths, schema bodies, or hidden tool names.

#### Scenario: Child startup logs bounded counts

- **WHEN** a subagent begins a run
- **THEN** one structured diagnostic records its three tool counts
- **AND** the event contains no authored payload or path
