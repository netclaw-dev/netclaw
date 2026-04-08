## Why

Netclaw's skill scanner currently drops several bad states silently: unreadable
files are skipped, duplicate skill names are ignored by first-wins behavior,
and the scanner can follow filesystem layouts that were never intended to be
trusted skill content. That violates the repo's fail-loudly posture on a
security-adjacent path that shapes the agent's procedural context.

Source PRDs: PRD-001, PRD-002.

## What Changes

- Harden skill discovery so only canonical, in-root, non-symlink skill paths are
  accepted during registry rebuilds and startup scans.
- Make scan failures explicit by returning structured issues for malformed
  skills, unreadable files, duplicate names, and frontmatter identity mismatch.
- Tighten skill identity validation so `skill_manage` rejects content whose
  frontmatter name does not match the target skill name.
- Ensure rescan callers surface degraded skill inventory state instead of
  silently rebuilding from a partial set.
- Keep MVP scope limited to local scanning and reporting; no feed signing,
  remote trust expansion, or content-classification engine is introduced here.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `skill-trust-tiers`: add hardened scan rules for canonical path handling,
  symlink rejection, duplicate detection, and structured issue reporting.
- `skill-tools`: require frontmatter identity to match the managed skill name
  and surface rescan issues after mutations.

## Impact

- `src/Netclaw.Actors/Skills/SkillScanner.cs` - canonical path checks,
  duplicate detection, structured scan results.
- `src/Netclaw.Actors/Tools/SkillManageTool.cs` - frontmatter/name validation
  and rescan issue handling.
- `src/Netclaw.Daemon/Program.cs` and
  `src/Netclaw.Daemon/Services/SystemSkillSyncService.cs` - consume scan issues
  during startup and registry rebuild.
- `src/Netclaw.Actors.Tests/Skills/SkillScannerTests.cs` and related tool tests
  - cover duplicate, symlink, unreadable-file, and mismatch cases.
- Operational impact: startup and tool-driven rescans now produce explicit
  degraded-skill diagnostics instead of silently omitting rejected skills.
