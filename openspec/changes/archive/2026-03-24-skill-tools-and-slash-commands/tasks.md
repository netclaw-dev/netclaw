## Tasks

### 1. Extend SkillFrontmatter and SkillEntry with invocation fields

- [x] Add `disable-model-invocation`, `invocable`, `argument-hint` to
      `SkillFrontmatter` in `SkillScanner.cs` with `[YamlMember]` aliases
- [x] Add `bool DisableModelInvocation` (default false), `bool UserInvocable`
      (default true), `string? ArgumentHint` to `SkillEntry.cs`
- [x] Update `BuildEntryFromFrontmatter()` to populate new fields
- [x] Verify: skill with `disable-model-invocation: true` parses correctly
- [x] Verify: skill without invocation fields gets defaults

**Acceptance:** New frontmatter fields parsed and available on `SkillEntry`.
(Completed in compressed-skill-index change, carried forward here.)

### 2. Implement skill_load tool

- [x] Create `src/Netclaw.Actors/Tools/SkillLoadTool.cs`
- [x] `[NetclawTool("skill_load", "...", Grant = "builtin")]`
- [x] Params: `string Name`
- [x] Look up skill in `SkillRegistry` by name (case-insensitive)
- [x] Read SKILL.md, strip frontmatter via `SkillScanner.ExtractBody()`
- [x] Return structured response: name, version, body, resource paths
- [x] Return error with available skill names if not found
- [x] Follow `SearchToolsTool.cs` pattern for tool structure
- [x] Register via `WithSkillTools()` in `ToolRegistrationExtensions`

**Acceptance:** Tool loads skill by name, returns structured body + resources.

### 3. Implement skill_read_resource tool

- [x] Create `src/Netclaw.Actors/Tools/SkillReadResourceTool.cs`
- [x] `[NetclawTool("skill_read_resource", "...", Grant = "builtin")]`
- [x] Params: `string SkillName`, `string ResourcePath`
- [x] Look up skill in registry, resolve absolute path from skill directory
- [x] Validate: path must start with `references/`, `scripts/`, or `assets/`
- [x] Validate: no `..` segments, no absolute paths
- [x] Validate: resolved path has no symlink segments
- [x] Return file content or descriptive error

**Acceptance:** Reads valid resource paths, rejects traversal/symlinks/absolute.

### 4. Implement skill_manage tool

- [x] Create `src/Netclaw.Actors/Tools/SkillManageTool.cs`
- [x] `[NetclawTool("skill_manage", "...", Grant = "builtin")]`
- [x] Params: `string Action`, `string Name`, `string? Content`,
      `string? FilePath`, `string? FileContent`,
      `string? OldString`, `string? NewString`, `bool ReplaceAll`
- [x] Action `create`: validate name + frontmatter, atomic write, content scan,
      reject if exists, re-scan
- [x] Action `edit`: full SKILL.md rewrite, same validation + scanning
- [x] Action `patch`: find-and-replace, unique match or ReplaceAll, supports
      FilePath for resource files
- [x] Action `delete`: remove directory, clean empty parents, re-scan
- [x] Action `write_file`: validate path prefix, reject traversal, create subdirs
- [x] Action `remove_file`: delete file, clean empty subdirectories
- [x] All write actions: reject writes to `.system/` directory
- [x] Inject `ISkillContentScanner` via constructor

**Acceptance:** All 6 actions implemented. Validation rejects bad input.
System skills are read-only. Re-scan triggers after mutations.

### 5. Add slash-command dispatch to SkillRegistry

- [x] Add `Dictionary<string, SkillEntry> _slashCommands` to `SkillRegistry`
- [x] Populate during `Register()`: add entry if `UserInvocable != false`
- [x] Key is skill `Name` (case-insensitive)
- [x] Clear on `Clear()`
- [x] Add `TryResolveSlashCommand(string input, out SkillEntry skill, out string remainder)`
- [x] Add `GetAvailableSlashCommands()` for error message generation

**Acceptance:** Slash commands resolve by name. Remainder text extracted.
Non-user-invocable skills excluded.

### 6. Add slash-command interception to LlmSessionActor

- [x] Add `SkillRegistry` as optional constructor parameter
- [x] In `SendUserMessage` handling, call `TryHandleSlashCommand()` before LLM
- [x] If matched: read SKILL.md body, inject as transient system message via
      `_slashCommandSkillContent`, pass remainder as user content
- [x] If unmatched: generate deterministic error listing available commands
      with argument hints, send as response, do NOT pass to LLM
- [x] Works for all message sources (Slack, webhooks, scheduled jobs)

**Acceptance:** `/netclaw-operations check health` loads skill + passes
remainder. `/nonexistent` returns error with available commands.

### 7. Tests

- [x] Unit test: slash-command dispatch resolves known command
- [x] Unit test: slash-command dispatch returns false for unknown command
- [x] Unit test: slash-command dispatch returns false for non-slash input
- [x] Unit test: slash-command handles command with no arguments
- [x] Unit test: non-user-invocable skills excluded from slash registry
- [x] Unit test: `GetAvailableSlashCommands()` lists user-invocable skills
- [x] Unit test: `Clear()` resets slash commands
- [x] All 640 Actors tests + 277 Daemon tests pass
- [x] `dotnet slopwatch analyze` — no new violations
