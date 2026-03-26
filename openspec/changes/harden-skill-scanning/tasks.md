## 1. Harden scanner boundaries

- [x] 1.1 Add a structured scan result model that returns accepted skills and rejected-item issues from `SkillScanner`
- [x] 1.2 Enforce canonical in-root path validation and reject symlinked skill directories, `SKILL.md` files, and resource trees during scanning
- [x] 1.3 Reject duplicate normalized skill names and frontmatter-name mismatches with explicit issue records instead of first-wins behavior

## 2. Surface degraded inventory state

- [x] 2.1 Update daemon startup and system skill sync rebuilds to consume structured scan results and log degraded inventory details
- [x] 2.2 Update `skill_manage` create/edit flows to validate frontmatter identity before write and report rescan issues after mutations
- [x] 2.3 Rebuild the skill registry and compressed menus from accepted entries only while preserving explicit issue visibility

## 3. Verify behavior

- [x] 3.1 Add scanner tests for duplicate names, frontmatter mismatch, symlink rejection, and unreadable-file reporting
- [x] 3.2 Add tool or startup-path tests proving degraded scan issues are surfaced instead of silently dropped
- [x] 3.3 Run relevant test suites and `dotnet slopwatch analyze`
