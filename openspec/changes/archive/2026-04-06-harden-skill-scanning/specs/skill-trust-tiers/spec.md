## ADDED Requirements

### Requirement: Hardened skill scan path validation
The skill scanner SHALL accept only skill files and resource paths whose
canonical filesystem locations remain under the configured skills root. The
scanner SHALL reject any skill whose directory, `SKILL.md`, or resource subtree
traverses a symlink or resolves outside the expected root.

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
- **GIVEN** an accepted skill contains a `references/`, `scripts/`, or `assets/` subtree that traverses a symlink
- **WHEN** the scanner enumerates resource files
- **THEN** the affected skill is rejected from the registry rebuild
- **AND** the scanner reports an issue identifying the resource path

### Requirement: Structured skill scan issue reporting
The skill scanner SHALL return structured issues alongside accepted entries so
callers can surface degraded inventory state. At minimum, issues SHALL identify
the rejected path and the rejection reason.

#### Scenario: Malformed frontmatter reported
- **GIVEN** a `SKILL.md` file contains unparseable YAML frontmatter
- **WHEN** the skill scanner runs
- **THEN** the skill is rejected
- **AND** the scanner returns an issue for that file instead of silently skipping it

#### Scenario: Duplicate skill names rejected
- **GIVEN** two discovered skills normalize to the same skill name
- **WHEN** the skill scanner runs
- **THEN** neither conflicting skill is registered
- **AND** the scanner returns an issue describing the duplicate name conflict

#### Scenario: Frontmatter name mismatch rejected
- **GIVEN** a skill directory name and frontmatter `name` field do not match after normalization
- **WHEN** the skill scanner runs
- **THEN** the skill is rejected
- **AND** the scanner returns an issue describing the mismatch

#### Scenario: Unreadable skill file reported
- **GIVEN** a discovered `SKILL.md` exists but cannot be read
- **WHEN** the skill scanner runs
- **THEN** the skill is rejected
- **AND** the scanner returns an issue describing the read failure
