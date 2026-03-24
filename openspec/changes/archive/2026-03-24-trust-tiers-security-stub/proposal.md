## Why

Netclaw needs trust tier infrastructure to support community and third-party
skills in future releases. Skills from different sources (system feed,
operator, community, external, agent-created) carry different trust levels
that affect visibility and security scanning requirements. The skill-authoring
system skill was removed during a recent cleanup and needs to come back to
document the complete Netclaw skill spec including the new invocation control
fields.

Ref: PRD-002 (Gateway Security), PRD-001 FR-006 (Layered System Prompt).

## What Changes

- Add `SkillTrustTier` enum (System, Operator, Community, External, Agent)
- Infer trust tier from skill directory location in `SkillScanner`
- Update `SkillScanner` to scan `.community`, `.external`, `.agent`
  directories (in addition to `.system`), but NOT `.quarantine`
- Add `ISkillContentScanner` interface stub with no-op implementation for
  future content scanning integration
- Restore `skill-authoring` system skill documenting the complete Netclaw
  skill frontmatter spec
- Update existing system skill frontmatter with invocation control fields

## Capabilities

### New Capabilities

- `skill-trust-tiers`: Trust tier enum, directory-based inference, and
  security scanner interface stub

### Modified Capabilities

(none — this is infrastructure + documentation only)

## Impact

- `src/Netclaw.Configuration/SkillTrustTier.cs` — new enum
- `src/Netclaw.Configuration/SkillEntry.cs` — add TrustTier property
- `src/Netclaw.Actors/Skills/SkillScanner.cs` — directory-based tier
  inference, expand hidden-directory allowlist
- `src/Netclaw.Security/Skills/ISkillContentScanner.cs` — new interface
- `src/Netclaw.Security/Skills/NoOpSkillContentScanner.cs` — new stub
- `feeds/skills/.system/files/skill-authoring/SKILL.md` — new/restored
- `feeds/skills/.system/files/*/SKILL.md` — update existing skill frontmatter
- GitHub issue to file for real content scanning implementation
