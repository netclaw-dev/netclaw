## Tasks

### 1. Create SkillTrustTier enum

- [ ] Create `src/Netclaw.Configuration/SkillTrustTier.cs`
- [ ] Values: `System = 0`, `Operator = 1`, `Community = 2`, `External = 3`, `Agent = 4`
- [ ] Add `SkillTrustTier TrustTier { get; init; }` to `SkillEntry.cs`
- [ ] Default to `SkillTrustTier.Operator` (safest default for existing code paths)

**Acceptance:** Enum exists, property on SkillEntry.

### 2. Implement directory-based trust tier inference

- [ ] In `SkillScanner.BuildEntryFromFrontmatter()`, infer tier from
      `relativePath`:
  - Category starts with `.system` → `System`
  - Category starts with `.community` → `Community`
  - Category starts with `.external` → `External`
  - Category starts with `.agent` → `Agent`
  - Null category (root-level) → `Operator`
  - Any other category → `Operator`
- [ ] Update Pass 2 hidden-directory filter (line 71) to allow:
      `.system`, `.community`, `.external`, `.agent`
- [ ] Explicitly exclude `.quarantine` (add to skip condition)
- [ ] Verify: skills in each directory get correct tier
- [ ] Verify: `.quarantine` and `.unknown` directories are not scanned

**Acceptance:** Trust tier correctly inferred from directory. Only allowed
hidden directories are scanned.

### 3. Create ISkillContentScanner interface and no-op implementation

- [ ] Create `src/Netclaw.Security/Skills/ISkillContentScanner.cs`
  ```csharp
  public interface ISkillContentScanner
  {
      Task<SkillScanResult> ScanAsync(string skillName, string content, CancellationToken ct);
  }
  public sealed record SkillScanResult(bool IsAllowed, string? Reason);
  ```
- [ ] Create `src/Netclaw.Security/Skills/NoOpSkillContentScanner.cs`
  - Returns `new SkillScanResult(true, null)` for all inputs
- [ ] Register `NoOpSkillContentScanner` as `ISkillContentScanner` in DI
      (`Program.cs` or service defaults)

**Acceptance:** Interface exists, no-op registered in DI.

### 4. Restore skill-authoring system skill

- [ ] Create `feeds/skills/.system/files/skill-authoring/SKILL.md`
- [ ] Document complete Netclaw skill spec:
  - AgentSkills.io directory layout (`skill-name/SKILL.md`)
  - Required frontmatter: `name`, `description`
  - Optional frontmatter: `license`, `compatibility`, `allowed-tools`,
    `metadata`, `disable-model-invocation`, `user-invocable`, `argument-hint`
  - Progressive disclosure: `references/`, `scripts/`, `assets/`
  - Invocation model: name = slash command, control flags explained
  - When to create a skill vs memory vs identity file
  - Using `skill_manage` tool for creation
  - Trust tiers overview
- [ ] Set `metadata.version: 1.0.0` in frontmatter

**Acceptance:** Skill-authoring skill exists with complete frontmatter spec.

### 5. Update existing system skill frontmatter

- [ ] `feeds/skills/.system/files/netclaw-operations/SKILL.md`:
      add `disable-model-invocation: true`
- [ ] `feeds/skills/.system/files/netclaw-diagnostics/SKILL.md`:
      add `disable-model-invocation: true`
- [ ] `feeds/skills/.system/files/netclaw-manual/SKILL.md`:
      add `user-invocable: false`
- [ ] `feeds/skills/.system/files/netclaw-memory/SKILL.md`:
      keep defaults (model-invocable, user-invocable)
- [ ] `feeds/skills/.system/files/search-citation/SKILL.md`:
      keep defaults (model-invocable, user-invocable)
- [ ] Bump `metadata.version` on any modified skill

**Acceptance:** Existing skills have appropriate invocation control fields.

### 6. File GitHub issue for real content scanning

- [ ] Create issue: "Implement skill content scanning using shared prompt
      injection detection infrastructure"
- [ ] Reference `ISkillContentScanner` interface
- [ ] Reference `IContentScanner` and `IPromptInjectionDetector` in
      `Netclaw.Security`
- [ ] Note: covers both webhook and skill scanning use cases
- [ ] Link to this change as the stub implementation

**Acceptance:** Issue filed with clear scope and references.

### 7. Tests

- [ ] Unit test: `SkillTrustTier` enum values ordered correctly
- [ ] Unit test: skills in `.system/` get `System` tier
- [ ] Unit test: skills in root get `Operator` tier
- [ ] Unit test: skills in `.community/` get `Community` tier
- [ ] Unit test: skills in `.external/` get `External` tier
- [ ] Unit test: skills in `.agent/` get `Agent` tier
- [ ] Unit test: `.quarantine/` directory not scanned
- [ ] Unit test: `.unknown/` hidden directory not scanned
- [ ] Unit test: `NoOpSkillContentScanner` returns `IsAllowed = true`
- [ ] `dotnet slopwatch analyze` — no new violations
