# progressive-tool-disclosure Specification

## Purpose

Define bounded tool schema exposure and actor-local deferred tool activation.

## Requirements

### Requirement: Tool registrations declare an exposure tier

Every registered tool SHALL have a `Core` or `Deferred` exposure tier. A
registration path that does not select `Core` SHALL select `Deferred`. The tier
SHALL NOT replace or widen deployment, audience, grant, or invocation policy.

#### Scenario: New first-party tool defaults to deferred

- **GIVEN** a first-party tool is registered without a core selection
- **WHEN** a session builds its initial model tool set
- **THEN** the new tool is absent from that initial set
- **AND** current policy controls whether the tool is discoverable

#### Scenario: Core selection does not grant a tool

- **GIVEN** current audience policy denies a core tool
- **WHEN** the session builds its initial model tool set
- **THEN** the denied tool is absent
- **AND** search and load do not reveal or activate it

### Requirement: Sessions start with a bounded workspace core

The initial model tool set SHALL contain policy-exposed definitions for
`search_tools`, `load_tool`, `skill_load`, `skill_read_resource`,
`set_working_directory`, `file_read`, `file_list`, `file_write`, `file_edit`,
and `shell_execute`. Other first-party and MCP tools SHALL be deferred unless a
later specification adds them to the core.

#### Scenario: Specialty tools are not eagerly exposed

- **GIVEN** specialty and MCP tools are registered
- **WHEN** a new Personal session begins
- **THEN** their schemas are absent from the initial set
- **AND** policy-exposed tools remain searchable by intent

#### Scenario: Core snapshot stays bounded

- **WHEN** the repository first-party catalog is tested
- **THEN** the core name snapshot equals the specified set
- **AND** another first-party registration does not change that snapshot

### Requirement: Search and load apply the complete exposure policy

`search_tools` and `load_tool` SHALL apply deployment switches, audience
allowlists, MCP grants, channel restrictions, and subagent restrictions before
they return or activate a tool. Results SHALL NOT reveal a hidden tool name or
schema. Loading SHALL add only the definition to the actor exposure set. Normal
authorization SHALL still run at dispatch.

#### Scenario: Hidden tool cannot be enumerated or loaded

- **GIVEN** a Team session cannot use a registered specialty tool
- **WHEN** it searches for and attempts to load the tool
- **THEN** neither response confirms that the hidden tool exists
- **AND** the model tool set is unchanged

#### Scenario: Loaded tool still requires invocation authority

- **GIVEN** a deferred tool is loadable and requires approval
- **WHEN** the model loads and calls the tool
- **THEN** the call enters the normal approval pipeline
- **AND** loading does not satisfy approval

### Requirement: Deferred exposure is actor-local and recoverable

Loaded deferred tools SHALL be transient actor state. Main-session leases SHALL
use the configured limits. Subagent-loaded tools SHALL exist only for that child
run. Recovery SHALL reseed the core and SHALL NOT require a durable migration.

#### Scenario: Main model failure evicts deferred first-party tool

- **GIVEN** a main session loaded a deferred first-party tool
- **WHEN** an LLM call fails and resets the cache
- **THEN** the next model request omits that tool
- **AND** the core remains available

#### Scenario: Child completion discards loaded tools

- **GIVEN** a subagent loads a deferred tool
- **WHEN** that child stops and another child starts
- **THEN** the new child receives only its policy-exposed core
- **AND** it does not inherit the prior child lease
