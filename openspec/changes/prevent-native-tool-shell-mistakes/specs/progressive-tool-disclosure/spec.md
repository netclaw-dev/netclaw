## MODIFIED Requirements

The terms in these requirements use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

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

## ADDED Requirements

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
