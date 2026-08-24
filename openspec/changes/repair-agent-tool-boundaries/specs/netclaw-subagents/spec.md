## MODIFIED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Subagents use progressive tool disclosure

A subagent SHALL begin with the same policy-exposed core tool set as a main session, minus tools prohibited by subagent policy. It SHALL NOT eagerly receive every discoverable first-party or MCP tool. `search_tools` and `load_tool` SHALL activate deferred schemas only in that child actor's ephemeral exposure set.

A child policy denial SHALL produce the same `access_denied` receipt category as a parent policy denial. A replay that claims child catalog behavior SHALL create a real child actor and inspect that child's model-visible tools.

#### Scenario: Child starts with core rather than full catalog

- **GIVEN** the daemon has more than one hundred visible specialty and MCP tools
- **WHEN** a subagent starts
- **THEN** its first model request contains only its allowed core tools
- **AND** `search_tools` can find allowed deferred capabilities

#### Scenario: Child loads one deferred tool

- **GIVEN** a subagent knows the exact name of a visible deferred tool
- **WHEN** it loads that exact tool
- **THEN** the next child request contains the core plus that tool
- **AND** unrelated deferred schemas remain absent

#### Scenario: Child cannot discover recursive delegation

- **GIVEN** `spawn_agent` is registered for the parent session
- **WHEN** a subagent searches for or attempts to load it
- **THEN** the response does not confirm or activate `spawn_agent`

#### Scenario: Child denial matches parent category

- **GIVEN** policy denies the same tool for a parent and a child
- **WHEN** each actor invokes that tool
- **THEN** each receipt category is `access_denied`
- **AND** neither actor records successful activity

#### Scenario: Replay inspects a real child catalog

- **GIVEN** a regression fixture asserts subagent catalog behavior
- **WHEN** the fixture executes
- **THEN** it creates a subagent through the production spawn path
- **AND** it asserts the child model request omits the hidden tool
