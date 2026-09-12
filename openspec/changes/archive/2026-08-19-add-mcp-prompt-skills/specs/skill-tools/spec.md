## MODIFIED Requirements

### Requirement: Logical model-facing skill access

For non-Public audiences with the skills subsystem enabled, normal model-initiated skill access SHALL use the registered logical skill name rather than a physical storage path. File-backed inline skills SHALL return their instruction body through `skill_load`. MCP prompt skills SHALL render through `prompts/get` on their recorded server generation. Skills declaring valid `metadata.subagent` routing SHALL execute through `skill_load` with a non-empty task. Listed file resources SHALL be read through `skill_read_resource` using the logical skill name and a safe relative resource path.

#### Scenario: File-backed inline skill loads by logical name

- **GIVEN** an inline file skill accepted from any configured file source
- **WHEN** the model calls `skill_load` with its logical name
- **THEN** the runtime reads the file source path
- **AND** returns the skill instructions without requiring the model to know the physical origin

#### Scenario: MCP prompt skill loads by logical name

- **GIVEN** an MCP prompt skill in the registered skill snapshot
- **WHEN** the model calls `skill_load` with its logical name and valid arguments
- **THEN** the runtime renders the prompt through its MCP source
- **AND** returns the attributed prompt instructions without a physical path

#### Scenario: Routed skill activates by logical name

- **GIVEN** a skill with valid `metadata.subagent`
- **WHEN** the model calls `skill_load` with its logical name and a non-empty task
- **THEN** the runtime executes the routed subagent path
- **AND** does not return or execute the inline path for the same activation

#### Scenario: Skill resource reads by logical name

- **GIVEN** a registered file skill exposes `references/guide.md`
- **WHEN** the model calls `skill_read_resource` with the logical skill name and `references/guide.md`
- **THEN** the runtime resolves the path beneath the registered file source directory
- **AND** applies existing path traversal and audience protections

#### Scenario: Explicit physical inspection remains available

- **GIVEN** a non-Public user explicitly asks to inspect a physical skill file
- **WHEN** the model uses an audience-authorized filesystem tool for that request
- **THEN** the request is governed by the normal filesystem access policy
- **AND** the logical skill contract does not redefine that explicit inspection as skill activation

## ADDED Requirements

### Requirement: skill_load MCP prompt arguments

`skill_load` SHALL accept an optional string argument map for an MCP prompt skill.
It SHALL validate the map against the published prompt descriptor before `prompts/get`.

#### Scenario: Required arguments pass unchanged

- **GIVEN** an MCP prompt requires argument `property`
- **WHEN** the model loads the skill with `property: petabridge-com`
- **THEN** the adapter passes that value to `prompts/get` unchanged

#### Scenario: Required argument is absent

- **GIVEN** an MCP prompt requires argument `property`
- **WHEN** the model loads the skill without that key
- **THEN** `skill_load` returns a clear missing-argument error
- **AND** it does not call `prompts/get`

#### Scenario: Unknown argument is present

- **GIVEN** an MCP prompt declares no argument named `tenant`
- **WHEN** the model loads the skill with a `tenant` key
- **THEN** `skill_load` returns a clear unknown-argument error
- **AND** it does not call `prompts/get`

#### Scenario: File skill receives prompt arguments

- **GIVEN** a file-backed skill
- **WHEN** the model passes a non-empty prompt argument map to `skill_load`
- **THEN** the tool returns a clear source-mismatch error
- **AND** it does not load the file
