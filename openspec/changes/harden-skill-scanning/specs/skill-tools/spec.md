## ADDED Requirements

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
After a mutating `skill_manage` operation rebuilds the skill registry, the tool
SHALL surface any scan issues discovered during that rebuild instead of silently
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
