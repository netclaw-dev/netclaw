## Why

Netclaw already exposes `skill_manage` and system skill feed sync as trusted ways to
write prompt-bearing skill content onto disk, but the registered
`ISkillContentScanner` is still a no-op. That leaves a gap in PRD-002's
self-configuration safety and prompt-injection threat model: skill files can be
created or updated without any enforced content screening even though those files
later shape agent behavior.

Source PRDs: PRD-001, PRD-002, PRD-007.

## What Changes

- Replace the no-op skill content scanner with a real, deterministic enforcement
  pipeline for `SKILL.md` bodies and skill resource files written by Netclaw.
- Enforce content scanning on all skill mutation paths managed by Netclaw:
  `skill_manage` create/edit/patch/write_file and system skill feed sync.
- Define a skill-file content policy that allows only bounded UTF-8 text files,
  rejects binary or unsupported resource payloads, and runs prompt-injection
  detection on prompt-facing content.
- Require fail-closed behavior when scanning fails or the detector errors, while
  preserving the previously accepted on-disk skill version.
- Surface rejection reasons in tool output and daemon logs so unsafe skill updates
  are diagnosable instead of silently skipped.
- Keep scope limited to Netclaw-controlled write paths; retroactive quarantine of
  manually edited on-disk skills and new `doctor` surfaces are out of scope.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `skill-tools`: require enforced content scanning for skill mutations and
  resource writes before any file is persisted.
- `skill-trust-tiers`: replace the default no-op scanner contract with an
  enforced scanner policy and extend it to feed-synced skill ingestion.

## Impact

- `src/Netclaw.Security/Skills/*` - production scanner implementation, result
  model, and DI registration.
- `src/Netclaw.Actors/Tools/SkillManageTool.cs` - enforce scans on create, edit,
  patch, and `write_file` flows with clear rejection messages.
- `src/Netclaw.Daemon/Services/SystemSkillSyncService.cs` - stage downloaded
  skills/resources through the same scanner before replacing on-disk copies.
- `src/Netclaw.Security.Tests/*`, `src/Netclaw.Actors.Tests/Tools/*`, and
  `src/Netclaw.Daemon.Tests/Services/*` - coverage for allowed text content,
  rejected binary payloads, prompt-injection blocks, detector failure handling,
  and sync rollback behavior.
- `feeds/skills/.system/files/skill-authoring/SKILL.md` - document the enforced
  scanner rules for authored skills and resource files.
- Operational impact: rejected skill updates become explicit warnings/errors
  rather than silently accepted content.
