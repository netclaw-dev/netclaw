## MODIFIED Requirements

The terms in this requirement use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Sessions start with a bounded workspace core

The initial model tool set SHALL contain policy-exposed definitions for `search_tools`, `load_tool`, `skill_load`, `skill_read_resource`, `set_working_directory`, `file_search`, `file_read`, `file_list`, `file_write`, `file_edit`, `tool_output_read`, and `shell_execute`. Other first-party and MCP tools SHALL be deferred unless a later specification adds them to the core. The core SHALL NOT include `json_read` or `file_read_many`.

`json_read` and `file_read_many` appear here only as negative regression names.
This change removes their registrations and schemas; it does not defer them.

Core membership example:

```text
initial core = [search_tools, load_tool, ..., shell_execute]

register ExampleSpecialtyTool with the default Deferred tier
  -> initial core is unchanged
  -> search_tools may find ExampleSpecialtyTool when policy allows it
  -> load_tool may add its schema to one actor

register ExampleCoreTool explicitly as Core
  -> the exact core snapshot changes
  -> that change requires a deliberate specification update
```

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
- **AND** search, load, and direct dispatch do not resurrect either removed name

### Requirement: Search and load apply the complete exposure policy

`search_tools` and `load_tool` SHALL apply deployment switches, audience allowlists, MCP grants, channel restrictions, and subagent restrictions before they return or activate a tool. Results SHALL NOT reveal a hidden tool name or schema. Loading SHALL add only the definition to the actor exposure set. Normal authorization SHALL still run at dispatch.

Tool guidance SHALL direct an agent to call `load_tool` when it knows the exact tool name. It SHALL direct the agent to call `search_tools` only when it knows an intent but not a name. A known deferred name MAY reach normal dispatch without a prior load, but dispatch SHALL NOT expose its schema or bypass authorization.

When retention and maximum-count tuning are positive, a main session SHALL
retain a loaded deferred schema for `Session.Tuning.DiscoveredToolRetentionTurns`
future user turns. The default is three. Reloading the same tool refreshes that
lease. A non-positive retention or maximum-count value disables cross-turn
retention. The default maximum is twelve loaded schemas; exceeding the cap
evicts the oldest. An LLM failure or session recovery SHALL discard actor-local
loaded schemas. Subagent exposure follows the separate child-run lifetime in
the `netclaw-subagents` specification.

Discovery, exposure, and authority examples:

| Model state and action | Required behavior |
|---|---|
| Knows exact allowed name `list_reminders`; calls `load_tool(Name = "list_reminders")` | Expose that schema without a preceding search. |
| Knows only “inspect scheduled tasks”; calls `search_tools(Query = "scheduled tasks")` | Return policy-filtered candidate descriptions, then load one exact result. |
| Searches for a policy-hidden tool | Return no name, schema, or existence hint. |
| Loads an allowed tool that still requires approval, then calls it | Enter normal approval; loading grants nothing. |
| Recalls and directly calls an allowed Deferred name before loading | Dispatch may authorize the call, but the schema does not become loaded merely because dispatch recognized the name. |
| Loads a tool and then the LLM call fails | Evict the loaded schema instead of carrying a possibly harmful exposure set forward. |
| Loads a thirteenth tool with the default maximum of twelve | Retain the newest twelve and evict the oldest loaded schema. |

Default lease example:

```text
user turn 1:
  load_tool(Name = "list_reminders")
  -> schema is available to the next model request in turn 1

user turns 2, 3, and 4:
  -> schema remains available

user turn 5 without another load:
  -> schema is absent

session recovery or LLM failure before turn 5:
  -> schema is absent immediately
```

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

#### Scenario: Default loaded-tool lease expires after three future turns

- **GIVEN** the main session uses the default retention value of three
- **AND** it loads one policy-visible Deferred tool
- **WHEN** three later user turns complete without another load
- **THEN** the tool schema is available on those three turns
- **AND** it is absent from the fourth later user turn

#### Scenario: Zero retention requires activation each turn

- **GIVEN** `DiscoveredToolRetentionTurns` is zero
- **WHEN** a main session loads a Deferred tool and begins a later user turn
- **THEN** the later turn does not retain that schema
- **AND** the earlier load still grants no execution authority
