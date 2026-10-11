## ADDED Requirements

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
