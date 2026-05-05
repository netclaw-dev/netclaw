## Tasks

### 1. Create SkillTrustTier enum

- [x] Create `src/Netclaw.Configuration/SkillTrustTier.cs`
- [x] Values: `System = 0`, `Operator = 1`, `Community = 2`, `External = 3`, `Agent = 4`
- [x] Add `SkillTrustTier TrustTier { get; init; }` to `SkillEntry.cs`
- [x] Default to `SkillTrustTier.Operator` (safest default for existing code paths)

**Acceptance:** Enum exists, property on SkillEntry.

### 2. Implement directory-based trust tier inference

- [x] In `SkillScanner.BuildEntryFromFrontmatter()`, infer tier from
      `relativePath`:
  - Category starts with `.system` → `System`
  - Category starts with `.community` → `Community`
  - Category starts with `.external` → `External`
  - Category starts with `.agent` → `Agent`
  - Null category (root-level) → `Operator`
  - Any other category → `Operator`
- [x] Update Pass 2 hidden-directory filter (line 71) to allow:
      `.system`, `.community`, `.external`, `.agent`
- [x] Explicitly exclude `.quarantine` (add to skip condition)
- [x] Verify: skills in each directory get correct tier
- [x] Verify: `.quarantine` and `.unknown` directories are not scanned

**Acceptance:** Trust tier correctly inferred from directory. Only allowed
hidden directories are scanned.

### 3. Create ISkillContentScanner interface and no-op implementation

- [x] Create `src/Netclaw.Security/Skills/ISkillContentScanner.cs`
  ```csharp
  public interface ISkillContentScanner
  {
      Task<SkillScanResult> ScanAsync(string skillName, string content, CancellationToken ct);
  }
  public sealed record SkillScanResult(bool IsAllowed, string? Reason);
  ```
- [x] Create `src/Netclaw.Security/Skills/NoOpSkillContentScanner.cs`
  - Returns `new SkillScanResult(true, null)` for all inputs
- [x] Register `NoOpSkillContentScanner` as `ISkillContentScanner` in DI
      (`Program.cs` or service defaults)

**Acceptance:** Interface exists, no-op registered in DI.

### 4. Restore skill-authoring system skill

- [x] Create `feeds/skills/.system/files/skill-authoring/SKILL.md`
- [x] Document complete Netclaw skill spec:
  - AgentSkills.io directory layout (`skill-name/SKILL.md`)
  - Required frontmatter: `name`, `description`
  - Optional frontmatter: `license`, `compatibility`, `allowed-tools`,
    `metadata`, `disable-model-invocation`, `invocable`, `argument-hint`
  - Progressive disclosure: `references/`, `scripts/`, `assets/`
  - Invocation model: name = slash command, control flags explained
  - When to create a skill vs memory vs identity file
  - Using `skill_manage` tool for creation
  - Trust tiers overview
- [x] Set `metadata.version: 1.0.0` in frontmatter

**Acceptance:** Skill-authoring skill exists with complete frontmatter spec.

### 5. Update existing system skill frontmatter

- [x] `feeds/skills/.system/files/netclaw-operations/SKILL.md`:
      add `disable-model-invocation: true`
- [x] `feeds/skills/.system/files/netclaw-diagnostics/SKILL.md`:
      N/A — skill does not exist (removed during recent cleanup)
- [x] `feeds/skills/.system/files/netclaw-manual/SKILL.md`:
      N/A — skill does not exist (removed during recent cleanup)
- [x] `feeds/skills/.system/files/netclaw-memory/SKILL.md`:
      keep defaults (model-invocable, user-invocable)
- [x] `feeds/skills/.system/files/search-citation/SKILL.md`:
      keep defaults (model-invocable, user-invocable)
- [x] Bump `metadata.version` on any modified skill

**Acceptance:** Existing skills have appropriate invocation control fields.

### 6. File GitHub issue for real content scanning

- [x] Create issue: "Implement skill content scanning using shared prompt
      injection detection infrastructure" — https://github.com/netclaw-dev/netclaw/issues/395
- [x] Reference `ISkillContentScanner` interface
- [x] Reference `IContentScanner` and `IPromptInjectionDetector` in
      `Netclaw.Security`
- [x] Note: covers both webhook and skill scanning use cases
- [x] Link to this change as the stub implementation

**Acceptance:** Issue filed with clear scope and references.

### 7. Tests

- [x] Unit test: `SkillTrustTier` enum values ordered correctly
- [x] Unit test: skills in `.system/` get `System` tier
- [x] Unit test: skills in root get `Operator` tier
- [x] Unit test: skills in `.community/` get `Community` tier
- [x] Unit test: skills in `.external/` get `External` tier
- [x] Unit test: skills in `.agent/` get `Agent` tier
- [x] Unit test: `.quarantine/` directory not scanned
- [x] Unit test: `.unknown/` hidden directory not scanned
- [x] Unit test: `NoOpSkillContentScanner` returns `IsAllowed = true`
- [x] `dotnet slopwatch analyze` — no new violations
