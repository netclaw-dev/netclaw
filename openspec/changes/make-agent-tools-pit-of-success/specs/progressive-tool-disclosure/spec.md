## ADDED Requirements

The terms in these requirements use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).
Later changes modify the exact Core set and add parent-only `attach_file`.

### Requirement: Tool registrations declare an exposure tier

Every registered tool SHALL have an exposure tier of `Core` or `Deferred`.
Registration paths that do not explicitly select `Core` SHALL select
`Deferred`. Exposure tier SHALL NOT replace or widen deployment, audience,
grant, or invocation policy.

#### Scenario: New first-party tool defaults to deferred

- **GIVEN** a first-party tool is registered without an explicit core selection
- **WHEN** a session builds its initial model tool set
- **THEN** the new tool is absent from that initial set
- **AND** the tool remains discoverable only when current policy exposes it

#### Scenario: Core selection does not grant a tool

- **GIVEN** a core tool is denied by the current audience profile
- **WHEN** the session builds its initial model tool set
- **THEN** the denied tool is absent
- **AND** search and load do not reveal or activate it

### Requirement: Sessions start with a bounded workspace core

The initial model tool set SHALL contain the policy-exposed definitions for
`search_tools`, `load_tool`, `skill_load`, `skill_read_resource`,
`set_working_directory`, `file_read`, `file_list`, `file_write`, `file_edit`,
and `shell_execute`. Other first-party and MCP tools SHALL be deferred unless a
later specification explicitly adds them to the core.

#### Scenario: Specialty tools are not eagerly exposed

- **GIVEN** reminder, webhook, background-job, web, and MCP tools are registered
- **WHEN** a new Personal session begins
- **THEN** their schemas are absent from the initial model request
- **AND** the policy-exposed tools remain searchable by intent

#### Scenario: Core snapshot stays bounded

- **WHEN** the repository's first-party tool catalog is tested
- **THEN** the core tool-name snapshot equals the specified core set
- **AND** registering another first-party tool does not change that snapshot

### Requirement: Search and load apply the complete exposure policy

`search_tools` and `load_tool` SHALL apply deployment switches, audience
allowlists, MCP server and tool grants, channel restrictions, and subagent tool
restrictions before returning or activating a tool. Results SHALL NOT reveal a
hidden tool's name or schema. Loading a tool SHALL add only its definition to the
current actor's exposure set; normal authorization SHALL still run at dispatch.

#### Scenario: Hidden tool cannot be enumerated or loaded

- **GIVEN** a Team session is not allowed to use a registered specialty tool
- **WHEN** it searches for the tool by exact name and then attempts to load it
- **THEN** neither response confirms the hidden tool exists
- **AND** the model tool set is unchanged

#### Scenario: Loaded tool still requires invocation authority

- **GIVEN** a deferred tool is discoverable and loadable for a session
- **AND** its invocation requires user approval
- **WHEN** the model loads and then calls the tool
- **THEN** the call enters the normal approval pipeline
- **AND** loading does not satisfy or bypass approval

### Requirement: Deferred exposure is actor-local and recoverable

Loaded deferred tools SHALL be transient actor-owned state. Main-session leases
SHALL retain and evict any deferred tool using the existing configured limits.
Subagent-loaded tools SHALL live only for that child run. Recovery SHALL reseed
the core set and SHALL NOT require a durable schema migration.

#### Scenario: Main model failure evicts deferred first-party tool

- **GIVEN** a main session has loaded a deferred first-party tool
- **WHEN** an LLM call fails and the discovered-tool cache is reset
- **THEN** the tool is absent from the next model request
- **AND** the core set remains available

#### Scenario: Child completion discards loaded tools

- **GIVEN** a subagent loads a deferred tool during its run
- **WHEN** that subagent stops and another subagent starts
- **THEN** the new subagent receives only its policy-exposed core set
- **AND** it does not inherit the prior child tool lease
