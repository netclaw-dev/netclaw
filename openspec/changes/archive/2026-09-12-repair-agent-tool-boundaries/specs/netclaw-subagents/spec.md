## MODIFIED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Subagents use progressive tool disclosure

A subagent SHALL begin with the same policy-exposed core tool set as a main session, minus tools prohibited by subagent policy. It SHALL NOT eagerly receive every discoverable first-party or MCP tool. `search_tools` and `load_tool` SHALL activate deferred schemas only in that child actor's ephemeral exposure set.

A child policy denial SHALL produce the same `access_denied` receipt category as a parent policy denial. A replay that claims child catalog behavior SHALL create a real child actor and inspect that child's model-visible tools.

A schema loaded by a child SHALL remain available for later model iterations in
that same child run. It SHALL be discarded when the child completes, stops, or
fails. Child exposure does not use the main session's configurable user-turn
lease because a child run has no independent sequence of user turns.

Netclaw intentionally prohibits recursive `spawn_agent` calls. A child cannot
create another child, even when the parent audience can use `spawn_agent`. This
keeps one parent tool call responsible for one bounded child actor and prevents
recursive agent trees from multiplying inference requests on self-hosted models
with limited concurrency.

Concrete exposure examples:

```text
parent core: [file_read, shell_execute, spawn_agent, ...]
child core:  [file_read, shell_execute, ...]
             # spawn_agent is removed by child policy

child calls load_tool(Name = "list_reminders")
  -> next child model request includes list_reminders
  -> later iterations in this same child still include list_reminders

child completes; parent starts a fresh child
  -> the fresh child does not inherit list_reminders

child calls search_tools(Query = "spawn agent")
  -> response does not confirm spawn_agent exists

child directly calls spawn_agent from recalled text
  -> dispatch cannot resolve or execute it from the child-private registry
```

#### Scenario: Child starts with core rather than full catalog

- **GIVEN** the daemon has more than one hundred visible specialty and MCP tools
- **WHEN** a subagent starts
- **THEN** its first model request contains only its allowed core tools
- **AND** `search_tools` can find allowed deferred capabilities

#### Scenario: Child loads one deferred tool

- **GIVEN** a subagent knows the exact name of a visible deferred tool
- **WHEN** it loads that exact tool
- **THEN** the next child request contains the core plus that tool
- **AND** later model iterations in the same child retain that tool
- **AND** unrelated deferred schemas remain absent

#### Scenario: Loaded child schema does not cross child lifetime

- **GIVEN** one child loads an allowed Deferred tool
- **WHEN** that child completes and the parent starts another child
- **THEN** the second child's first model request omits the loaded tool
- **AND** the first child created no durable or parent-owned exposure lease

#### Scenario: Child cannot discover recursive delegation

- **GIVEN** `spawn_agent` is registered for the parent session
- **WHEN** a subagent searches for or attempts to load it
- **THEN** the response does not confirm or activate `spawn_agent`
- **AND** a direct child dispatch cannot start a grandchild

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
