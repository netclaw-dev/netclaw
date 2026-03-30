## ADDED Requirements

### Requirement: skill_manage content scanning is enforced
The `skill_manage` tool SHALL enforce content scanning before persisting any
skill file written through `create`, `edit`, `patch`, or `write_file`. The tool
SHALL reject the mutation when the scanner reports unsafe content or when the
scanner itself fails.

#### Scenario: Reject create when skill body scan fails
- **WHEN** the agent calls `skill_manage(action: "create", ...)`
- **AND** the scanner rejects the proposed `SKILL.md` content
- **THEN** the tool returns a rejection reason
- **AND** no skill directory or file is persisted

#### Scenario: Reject patch when patched skill body becomes unsafe
- **GIVEN** a skill already exists
- **WHEN** the agent calls `skill_manage(action: "patch", ...)` targeting `SKILL.md`
- **AND** the patched content fails the content scan
- **THEN** the patch is rejected
- **AND** the existing `SKILL.md` remains unchanged

#### Scenario: Reject resource write with unsupported content
- **WHEN** the agent calls `skill_manage(action: "write_file", filePath: "assets/payload.bin", ...)`
- **AND** the file content is binary or otherwise disallowed by the skill content policy
- **THEN** the tool returns a rejection reason
- **AND** the resource file is not written

#### Scenario: Reject mutation when scanner errors
- **WHEN** the agent calls a mutating `skill_manage` action
- **AND** the content scanner throws or times out
- **THEN** the mutation is rejected
- **AND** the tool response explains that scanning failed

### Requirement: skill resource file policy
Skill resource files written through `skill_manage` SHALL be restricted to
approved text-based content for their subdirectory role. Binary payloads and
unsupported file types SHALL be rejected before write.

#### Scenario: Allow text script helper
- **WHEN** the agent calls `skill_manage(action: "write_file", filePath: "scripts/check.sh", ...)`
- **AND** the file is valid UTF-8 text matching an allowed script extension
- **THEN** the file is written successfully

#### Scenario: Allow markdown reference document
- **WHEN** the agent calls `skill_manage(action: "write_file", filePath: "references/checklist.md", ...)`
- **AND** the file is valid UTF-8 markdown under the configured size limit
- **THEN** the file is written successfully

#### Scenario: Reject oversized text asset
- **WHEN** the agent calls `skill_manage(action: "write_file", filePath: "assets/template.json", ...)`
- **AND** the content exceeds the configured skill resource size limit
- **THEN** the tool rejects the write
- **AND** reports the size-based validation failure
