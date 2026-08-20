## MODIFIED Requirements

### Requirement: Sessions start with a bounded workspace core

The initial model tool set SHALL contain policy-exposed definitions for `search_tools`, `load_tool`, `skill_load`, `skill_read_resource`, `set_working_directory`, `file_search`, `file_read`, `file_list`, `file_write`, `file_edit`, `tool_output_read`, and `shell_execute`. Other first-party and MCP tools SHALL be deferred unless a later specification adds them to the core. The core SHALL NOT include `json_read` or `file_read_many`.

#### Scenario: Specialty tools are not eagerly exposed

- **GIVEN** specialty and MCP tools are registered
- **WHEN** a new Personal session begins
- **THEN** their schemas are absent from the initial set
- **AND** policy-exposed tools remain searchable by intent

#### Scenario: Core snapshot stays bounded

- **WHEN** the repository first-party catalog is tested
- **THEN** the core name snapshot equals the specified set
- **AND** another first-party registration does not change that snapshot

#### Scenario: Removed bulk tools are absent

- **WHEN** a parent or child actor builds its core tool set
- **THEN** `json_read` and `file_read_many` are absent
- **AND** `file_read` remains available

### Requirement: Search and load apply the complete exposure policy

`search_tools` and `load_tool` SHALL apply deployment switches, audience allowlists, MCP grants, channel restrictions, and subagent restrictions before they return or activate a tool. Results SHALL NOT reveal a hidden tool name or schema. Loading SHALL add only the definition to the actor exposure set. Normal authorization SHALL still run at dispatch.

Tool guidance SHALL direct an agent to call `load_tool` when it knows the exact tool name. It SHALL direct the agent to call `search_tools` only when it knows an intent but not a name. A known deferred name MAY reach normal dispatch without a prior load, but dispatch SHALL NOT expose its schema or bypass authorization.

#### Scenario: Hidden tool cannot be enumerated or loaded

- **GIVEN** a Team session cannot use a registered specialty tool
- **WHEN** it searches for and attempts to load the tool
- **THEN** neither response confirms that the hidden tool exists
- **AND** the model tool set is unchanged

#### Scenario: Exact known name loads without search

- **GIVEN** the prompt index names an allowed deferred tool
- **WHEN** the agent needs that exact tool
- **THEN** guidance directs it to `load_tool` directly
- **AND** no shell probe is required

#### Scenario: Loaded tool still requires invocation authority

- **GIVEN** a deferred tool is loadable and requires approval
- **WHEN** the model loads and calls the tool
- **THEN** the call enters the normal approval pipeline
- **AND** loading does not satisfy approval

#### Scenario: Recalled deferred name still reaches authorization

- **GIVEN** a model calls an allowed registered deferred name before schema load
- **WHEN** dispatch resolves that name
- **THEN** the normal authorization pipeline decides the call
- **AND** no exposure lease grants authority
