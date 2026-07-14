## ADDED Requirements

### Requirement: Logical model-facing skill access

For non-Public audiences with the skills subsystem enabled, normal model-initiated skill access SHALL use the registered logical skill name rather than a physical storage path. Inline skills SHALL return their instruction body through `skill_load`; skills declaring valid `metadata.subagent` routing SHALL execute through `skill_load` with a non-empty task; listed resources SHALL be read through `skill_read_resource` using the logical skill name and a safe relative resource path.

#### Scenario: Inline skill loads by logical name

- **GIVEN** an inline skill accepted from any configured source
- **WHEN** the model calls `skill_load` with its logical name
- **THEN** the runtime reads the registered `SkillEntry.FilePath`
- **AND** returns the skill instructions without requiring the model to know the physical origin

#### Scenario: Routed skill activates by logical name

- **GIVEN** a skill with valid `metadata.subagent`
- **WHEN** the model calls `skill_load` with its logical name and a non-empty task
- **THEN** the runtime executes the routed subagent path
- **AND** does not return or execute the inline path for the same activation

#### Scenario: Skill resource reads by logical name

- **GIVEN** a registered skill exposes `references/guide.md`
- **WHEN** the model calls `skill_read_resource` with the logical skill name and `references/guide.md`
- **THEN** the runtime resolves the path beneath the registered `SkillEntry.SkillDirectory`
- **AND** applies existing path traversal and audience protections

#### Scenario: Explicit physical inspection remains available

- **GIVEN** a non-Public user explicitly asks to inspect a physical skill file
- **WHEN** the model uses an audience-authorized filesystem tool for that request
- **THEN** the request is governed by the normal filesystem access policy
- **AND** the logical skill contract does not redefine that explicit inspection as skill activation

### Requirement: Authoritative skill inventory refresh

Every in-process skill inventory refresh SHALL resolve the current enabled native, server-feed, and external sources, scan them with native greater than server-feed greater than external precedence, and update the registry and generated index from the same accepted result. Concurrent refresh requests SHALL NOT expose a partially rebuilt registry.

#### Scenario: Skill management preserves server-feed inventory

- **GIVEN** a server-feed skill and a native skill are registered
- **WHEN** `skill_manage` successfully mutates the native skill inventory
- **THEN** the refresh retains the server-feed skill
- **AND** the generated index contains both logical skill names

#### Scenario: Newly available feed directory participates in refresh

- **GIVEN** an enabled configured server feed whose managed directory appears after daemon startup
- **WHEN** any inventory refresh occurs
- **THEN** the current feed directory is included in the scan

#### Scenario: Native skill shadows server-feed skill

- **GIVEN** native and server-feed skills have the same logical name
- **WHEN** the inventory is refreshed
- **THEN** the native skill is registered
- **AND** the shadowed server-feed skill is reported through existing scan diagnostics

#### Scenario: Concurrent readers see a complete inventory snapshot

- **GIVEN** sessions can read the skill registry while a background refresh occurs
- **WHEN** the refreshed inventory replaces the previous inventory
- **THEN** each reader observes either the complete previous snapshot or the complete new snapshot
- **AND** no reader observes the registry between clear and repopulation
