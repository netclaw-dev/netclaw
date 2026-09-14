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

The initial parent-session model tool set SHALL contain policy-exposed definitions for
`search_tools`, `load_tool`, `skill_load`, `skill_read_resource`,
`set_working_directory`, `file_search`, `file_read`, `file_list`, `file_write`,
`file_edit`, `tool_output_read`, `attach_file`, and `shell_execute`. Other
first-party and MCP tools SHALL be deferred unless a later specification adds
them to the core. The core SHALL NOT include `json_read` or `file_read_many`.

The `skill_read_resource` name uses the glossary definition of a skill
resource. It reads an additional file through a permitted relative path. The
`skill_load` tool reads `SKILL.md` instead.

Sub-agent model tool sets SHALL exclude `attach_file` from core exposure,
discovery, loading, and direct dispatch until an internal attachment
handoff can deliver child attachments through the parent invocation.

#### Scenario: Specialty tools are not eagerly exposed

- **GIVEN** specialty and MCP tools are registered
- **WHEN** a new Personal session begins
- **THEN** their schemas are absent from the initial set
- **AND** policy-exposed tools remain searchable by intent

#### Scenario: Core snapshot stays bounded

- **WHEN** the repository first-party catalog is tested
- **THEN** the core name snapshot equals the specified set
- **AND** another first-party registration does not change that snapshot

#### Scenario: Child cannot report false attachment success

- **GIVEN** `attach_file` is registered as a parent-session Core tool
- **WHEN** a sub-agent searches, loads, or directly calls `attach_file`
- **THEN** the child does not discover or dispatch the tool
- **AND** the parent-session core still contains `attach_file`

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

### Requirement: Agent correction can expose one deferred first-party tool

The closed `UseNativeTool` remediation code and its `NativeToolSuggested` correction fact SHALL be able to request actor-local exposure of one policy-visible deferred first-party tool.

The actor SHALL record the correction result before activating the schema. It
SHALL recheck current exposure policy. It SHALL NOT persist the activation or
treat schema exposure as execution authority.

Required order:

```text
1. Record the model-facing correction result.
2. Read the separate exposure fact with the exact registered tool name.
3. Resolve the exact registration again.
4. Recheck current exposure policy.
5. Add only the schema to the current actor exposure set.
6. Run normal authorization if the model later calls the tool.
```

Exposure examples and counterexamples:

| Correction target | Required exposure result |
|---|---|
| Policy-visible Deferred `list_reminders` | Add only that schema to the next actor request. |
| Policy-visible Core `file_read` | Keep the existing Core schema. Add no duplicate. |
| A tool that policy hides before activation | Add no schema. Reveal no new tool detail. |
| A registration removed before activation | Add no schema. Execute nothing. |
| A later native call after activation | Run normal authorization. The exposure fact grants nothing. |

Main-session recovery and child completion discard the activated deferred
schema. They do not discard a main-session correction message that already
entered durable chat history.

#### Scenario: Deferred target appears on the next model request

- **GIVEN** a complete static shell call contains the exact executable name of a policy-visible deferred first-party tool
- **WHEN** the runtime returns the native-tool correction
- **THEN** the correction result enters model history
- **AND** the next model request contains the target schema
- **AND** calling that schema still enters normal authorization

#### Scenario: Hidden target is not exposed

- **GIVEN** a registered first-party tool is hidden or denied by current policy
- **WHEN** its exact name appears as a shell executable token
- **THEN** no native-tool correction confirms the tool
- **AND** no schema activation occurs

#### Scenario: Activation remains actor-local

- **GIVEN** a correction activates a deferred tool in one actor
- **WHEN** that child completes, the model call fails and evicts leases, or the session recovers
- **THEN** the later exposure set is rebuilt from the policy-filtered core
- **AND** the correction does not create durable exposure state
