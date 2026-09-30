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

### Requirement: Hardened skill scan path validation

The skill scanner SHALL accept only skill files and resource paths whose
canonical filesystem locations remain under the scanned skills root. The
native (`~/.netclaw/skills`) scan SHALL reject any skill whose directory,
`SKILL.md`, or resource subtree traverses a symlink or resolves outside the
root. A server-feed or external skill source SHALL follow its configured
`AllowSymlinks` setting, and SHALL still reject a path outside its root.

#### Scenario: Reject symlinked skill directory

- **GIVEN** a directory under `~/.netclaw/skills/` is a symlink to another location
- **WHEN** the skill scanner evaluates that directory
- **THEN** the directory is not registered as a skill
- **AND** the scanner reports an issue identifying symlink traversal

#### Scenario: Reject skill file outside root

- **GIVEN** a candidate `SKILL.md` resolves outside the configured skills root
- **WHEN** the skill scanner evaluates the candidate
- **THEN** the skill is rejected
- **AND** the scanner reports an issue identifying the out-of-root path

#### Scenario: Reject symlinked resource subtree

- **GIVEN** an accepted native skill contains a `references/`, `scripts/`, or `assets/` subtree that traverses a symlink
- **WHEN** the scanner enumerates resource files
- **THEN** the affected skill is rejected from the registry rebuild
- **AND** the scanner reports an issue identifying the resource path

### Requirement: Structured skill scan issue reporting

The skill scanner SHALL return structured issues alongside accepted entries so
callers can surface degraded inventory state. Each issue SHALL identify the
rejected path and the rejection reason.

Within one scanned root, a name collision SHALL resolve as follows:

- When exactly one of the colliding skills is in the `.system` category, that
  system skill SHALL be registered, and each other copy SHALL be rejected with
  a duplicate-name issue that names the system skill.
- Otherwise, one directory skill SHALL win over flat-file skills with the same
  name, and each flat file SHALL get a duplicate-name issue.
- Otherwise (two or more directory skills, or two or more flat files), no
  colliding skill SHALL be registered, and each SHALL get a duplicate-name
  issue.

Across roots, native skills SHALL win over server-feed skills, and server-feed
skills SHALL win over external skills (Requirement "Authoritative skill
inventory refresh").

The native scan SHALL require the frontmatter `name` to match the directory
name after normalization. Server-feed and external scans SHALL NOT require that
match.

#### Scenario: Malformed frontmatter reported

- **GIVEN** a `SKILL.md` file contains unparseable YAML frontmatter
- **WHEN** the skill scanner runs
- **THEN** the skill is rejected
- **AND** the scanner returns an issue for that file instead of silently skipping it

#### Scenario: System skill wins a name collision

- **GIVEN** `~/.netclaw/skills/.system/foo/SKILL.md` and a user skill `~/.netclaw/skills/foo/SKILL.md` both declare the name `foo`
- **WHEN** the skill scanner runs
- **THEN** the system skill `foo` is registered
- **AND** the scanner returns a duplicate-name issue for the user copy

#### Scenario: Duplicate non-system skill names rejected

- **GIVEN** two directory skills outside `.system` normalize to the same skill name
- **WHEN** the skill scanner runs
- **THEN** neither conflicting skill is registered
- **AND** the scanner returns an issue describing the duplicate name conflict

#### Scenario: Native frontmatter name mismatch rejected

- **GIVEN** a native skill directory name and frontmatter `name` field do not match after normalization
- **WHEN** the skill scanner runs
- **THEN** the skill is rejected
- **AND** the scanner returns an issue describing the mismatch

#### Scenario: Unreadable skill file reported

- **GIVEN** a discovered `SKILL.md` exists but cannot be read
- **WHEN** the skill scanner runs
- **THEN** the skill is rejected
- **AND** the scanner returns an issue describing the read failure
