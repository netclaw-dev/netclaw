# skill-trust-tiers Specification

## Purpose

Define trust tier classification for skills, directory-based tier inference,
and the security scanner interface for content validation.

## Requirements

### Requirement: Trust tier enum

The system SHALL define trust tiers for skills: System (0), Operator (1),
Community (2), External (3), Agent (4). Lower values indicate higher trust.

#### Scenario: Enum values ordered by trust

- **WHEN** comparing trust tiers
- **THEN** System < Operator < Community < External < Agent
- **AND** lower numeric values indicate higher trust

### Requirement: Directory-based trust tier inference

The system SHALL infer a skill's trust tier from its directory location within
the skills directory. A skill cannot self-declare its trust tier.

#### Scenario: System skill in .system directory

- **GIVEN** a skill at `~/.netclaw/skills/.system/netclaw-memory/SKILL.md`
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.TrustTier` is `SkillTrustTier.System`

#### Scenario: Operator skill in root directory

- **GIVEN** a skill at `~/.netclaw/skills/my-workflow/SKILL.md`
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.TrustTier` is `SkillTrustTier.Operator`

#### Scenario: Community skill in .community directory

- **GIVEN** a skill at `~/.netclaw/skills/.community/home-automation/SKILL.md`
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.TrustTier` is `SkillTrustTier.Community`

#### Scenario: External skill in .external directory

- **GIVEN** a skill at `~/.netclaw/skills/.external/third-party/SKILL.md`
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.TrustTier` is `SkillTrustTier.External`

#### Scenario: Agent skill in .agent directory

- **GIVEN** a skill at `~/.netclaw/skills/.agent/my-workflow/SKILL.md`
- **WHEN** the skill is scanned
- **THEN** `SkillEntry.TrustTier` is `SkillTrustTier.Agent`

### Requirement: Expanded hidden-directory scanning

The system SHALL scan `.community`, `.external`, and `.agent` hidden
directories in addition to `.system`. The `.quarantine` directory SHALL NOT
be scanned.

#### Scenario: Community directory scanned

- **GIVEN** skills exist in `~/.netclaw/skills/.community/`
- **WHEN** the skill scanner runs
- **THEN** skills in `.community/` are discovered and registered

#### Scenario: Quarantine directory excluded

- **GIVEN** skills exist in `~/.netclaw/skills/.quarantine/`
- **WHEN** the skill scanner runs
- **THEN** skills in `.quarantine/` are NOT discovered

#### Scenario: Unknown hidden directories excluded

- **GIVEN** a directory `~/.netclaw/skills/.unknown/` exists
- **WHEN** the skill scanner runs
- **THEN** skills in `.unknown/` are NOT discovered

### Requirement: Skill content scanner interface

The system SHALL define an `ISkillContentScanner` interface for validating
skill content before writes. A no-op implementation SHALL be provided as the
default.

#### Scenario: No-op scanner allows all content

- **GIVEN** the `NoOpSkillContentScanner` is registered
- **WHEN** `ScanAsync` is called with any content
- **THEN** the result is `IsAllowed = true`

#### Scenario: Scanner called on skill_manage create

- **WHEN** `skill_manage(action: "create")` is called
- **THEN** `ISkillContentScanner.ScanAsync()` is invoked with the skill content
- **AND** the write proceeds only if `IsAllowed = true`

#### Scenario: Scanner called on skill_manage edit

- **WHEN** `skill_manage(action: "edit")` is called
- **THEN** `ISkillContentScanner.ScanAsync()` is invoked with the new content
- **AND** the write proceeds only if `IsAllowed = true`
