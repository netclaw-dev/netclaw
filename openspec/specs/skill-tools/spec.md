# skill-tools Specification

## Purpose

Define the `skill_load`, `skill_read_resource`, and `skill_manage` tools for
structured skill access and management.
## Requirements
### Requirement: skill_load tool

The system SHALL provide `skill_load` only when the skills subsystem is enabled
for the deployment and exposed to the requesting audience. Public sessions SHALL
not use `skill_load` to enumerate or load hidden internal skills.

#### Scenario: skill_load unavailable to Public

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** tool definitions are built or the session attempts to use
  `skill_load`
- **THEN** `skill_load` is absent or denied for that session

#### Scenario: skill_load unavailable when skills runtime disabled

- **GIVEN** `SkillSync.Enabled` is `false` in config
- **WHEN** a Team session attempts to use `skill_load`
- **THEN** the tool is absent or denied because the skills subsystem is runtime-disabled

### Requirement: skill_read_resource tool

The system SHALL provide `skill_read_resource` only when the skills subsystem is
enabled for the deployment and exposed to the requesting audience. Public
sessions SHALL not use it to recover skill internals.

#### Scenario: skill_read_resource unavailable to Public

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** tool definitions are built or the session attempts to use
  `skill_read_resource`
- **THEN** `skill_read_resource` is absent or denied for that session

#### Scenario: skill index does not advertise hidden skill tools to Public

- **GIVEN** a session with `TrustAudience.Public`
- **WHEN** the prompt is assembled
- **THEN** the injected skill guidance does not instruct the model to use
  `skill_load` or `skill_read_resource`

### Requirement: skill_manage tool

The system SHALL provide a `skill_manage` tool with `Grant = "builtin"` that
supports 6 actions for skill CRUD and resource file management. All writes
SHALL target the user skills area only, never `.system/`.

#### Scenario: Create new skill

- **WHEN** the agent calls `skill_manage(action: "create", name: "my-workflow", content: "---\nname: my-workflow\ndescription: ...\n---\n# My Workflow\n...")`
- **THEN** the tool creates `~/.netclaw/skills/my-workflow/SKILL.md`
- **AND** validates frontmatter (name format, description required, description <= 1024 chars)
- **AND** uses atomic write (temp file + rename)
- **AND** re-scans the skills directory and rebuilds the registry

#### Scenario: Create skill with invalid frontmatter

- **WHEN** the agent calls `skill_manage(action: "create", content: "no frontmatter")`
- **THEN** the tool returns an error describing the validation failure
- **AND** no files are created

#### Scenario: Create skill with invalid name

- **WHEN** the agent calls `skill_manage(action: "create", name: "Invalid Name!")`
- **THEN** the tool returns an error — name must be lowercase alphanumeric + hyphens

#### Scenario: Edit existing skill

- **WHEN** the agent calls `skill_manage(action: "edit", name: "my-workflow", content: "...")`
- **THEN** the tool overwrites `SKILL.md` with validated content
- **AND** uses atomic write

#### Scenario: Patch skill content

- **WHEN** the agent calls `skill_manage(action: "patch", name: "my-workflow", oldString: "old text", newString: "new text")`
- **THEN** the tool replaces the first occurrence of `oldString` with `newString`
- **AND** fails if `oldString` is not found or not unique (unless `replaceAll: true`)

#### Scenario: Delete skill

- **WHEN** the agent calls `skill_manage(action: "delete", name: "my-workflow")`
- **THEN** the tool removes the `my-workflow/` directory
- **AND** cleans empty parent category directories
- **AND** re-scans and rebuilds registry

#### Scenario: Write resource file

- **WHEN** the agent calls `skill_manage(action: "write_file", name: "my-workflow", filePath: "references/checklist.md", fileContent: "...")`
- **THEN** the tool creates the file within the skill's directory
- **AND** validates path is within `references/`, `scripts/`, or `assets/`
- **AND** rejects path traversal attempts

#### Scenario: Remove resource file

- **WHEN** the agent calls `skill_manage(action: "remove_file", name: "my-workflow", filePath: "references/old-doc.md")`
- **THEN** the tool deletes the file
- **AND** cleans empty subdirectories

#### Scenario: Reject write to system skills

- **WHEN** the agent calls `skill_manage(action: "edit", name: "netclaw-memory")`
- **AND** `netclaw-memory` is in the `.system/` directory
- **THEN** the tool returns an error — system skills are read-only

#### Scenario: Content scanner integration point

- **WHEN** the agent calls `skill_manage(action: "create")` or `skill_manage(action: "edit")`
- **THEN** `ISkillContentScanner.ScanAsync()` is called on the content before writing
- **AND** if the scanner returns `IsAllowed = false`, the write is rejected

### Requirement: skill_manage identity validation

The `skill_manage` tool SHALL require the managed skill identity to remain
consistent across the tool arguments, directory name, and frontmatter content.
If frontmatter `name` is present for a create or edit operation, it SHALL match
the target skill name after normalization.

#### Scenario: Reject create with mismatched frontmatter name

- **WHEN** the agent calls `skill_manage(action: "create", name: "my-workflow", content: "---\nname: other-name\ndescription: ...\n---\n# My Workflow")`
- **THEN** the tool returns an error describing the name mismatch
- **AND** no files are written

#### Scenario: Reject edit with mismatched frontmatter name

- **GIVEN** a skill named `my-workflow` already exists
- **WHEN** the agent calls `skill_manage(action: "edit", name: "my-workflow", content: "---\nname: renamed-workflow\ndescription: ...\n---\n# My Workflow")`
- **THEN** the tool returns an error describing the name mismatch
- **AND** the existing skill file is not modified

### Requirement: skill_manage rescan issue visibility

After a mutating `skill_manage` operation rebuilds the skill registry, the tool SHALL surface any scan issues discovered during that rebuild instead of silently
refreshing the index from a partial set.

#### Scenario: Create reports unrelated degraded inventory

- **GIVEN** another skill under the skills directory is malformed and rejected during scan
- **WHEN** the agent successfully calls `skill_manage(action: "create", ... )`
- **THEN** the new skill is created if its own content is valid
- **AND** the tool response includes a warning that the registry rebuild has scan issues

#### Scenario: Edit reports accepted rebuild count and issues

- **GIVEN** an edit succeeds for the target skill
- **WHEN** the registry rebuild rejects one or more other skills
- **THEN** the tool response reports that the edit succeeded
- **AND** the response also reports that the rebuilt inventory is degraded

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
