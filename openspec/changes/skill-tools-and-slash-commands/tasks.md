## Tasks

### 1. Extend SkillFrontmatter and SkillEntry with invocation fields

- [ ] Add `disable-model-invocation`, `user-invocable`, `argument-hint` to
      `SkillFrontmatter` in `SkillScanner.cs` with `[YamlMember]` aliases
- [ ] Add `bool DisableModelInvocation` (default false), `bool UserInvocable`
      (default true), `string? ArgumentHint` to `SkillEntry.cs`
- [ ] Update `BuildEntryFromFrontmatter()` to populate new fields
- [ ] Verify: skill with `disable-model-invocation: true` parses correctly
- [ ] Verify: skill without invocation fields gets defaults

**Acceptance:** New frontmatter fields parsed and available on `SkillEntry`.

### 2. Implement skill_load tool

- [ ] Create `src/Netclaw.Actors/Tools/SkillLoadTool.cs`
- [ ] `[NetclawTool("skill_load", "...", Grant = "builtin")]`
- [ ] Params: `string Name`
- [ ] Look up skill in `SkillRegistry` by name (case-insensitive)
- [ ] Read SKILL.md, strip frontmatter via `SkillScanner.ExtractBody()`
- [ ] Return structured response: name, version, body, resource paths
- [ ] Return error with available skill names if not found
- [ ] Follow `SearchToolsTool.cs` pattern for tool structure
- [ ] Register in tool wiring (same path as other first-party tools)

**Acceptance:** Tool loads skill by name, returns structured body + resources.
Works in all audiences.

### 3. Implement skill_read_resource tool

- [ ] Create `src/Netclaw.Actors/Tools/SkillReadResourceTool.cs`
- [ ] `[NetclawTool("skill_read_resource", "...", Grant = "builtin")]`
- [ ] Params: `string SkillName`, `string ResourcePath`
- [ ] Look up skill in registry, resolve absolute path from skill directory
- [ ] Validate: path must start with `references/`, `scripts/`, or `assets/`
- [ ] Validate: no `..` segments, no absolute paths
- [ ] Validate: resolved path has no symlink segments
- [ ] Return file content or descriptive error

**Acceptance:** Reads valid resource paths, rejects traversal/symlinks/absolute.

### 4. Implement skill_manage tool

- [ ] Create `src/Netclaw.Actors/Tools/SkillManageTool.cs`
- [ ] `[NetclawTool("skill_manage", "...", Grant = "builtin")]`
- [ ] Params: `string Action`, `string Name`, `string? Content`,
      `string? Category`, `string? FilePath`, `string? FileContent`,
      `string? OldString`, `string? NewString`, `bool ReplaceAll`
- [ ] Action `create`:
  - [ ] Validate name format (lowercase alphanumeric + hyphens, max 64 chars)
  - [ ] Validate frontmatter (name + description required, description <= 1024)
  - [ ] Create directory at `~/.netclaw/skills/{name}/`
  - [ ] Atomic write: temp file + `File.Move` with overwrite
  - [ ] Call `ISkillContentScanner.ScanAsync()` before writing
  - [ ] Reject if scanner returns `IsAllowed = false`
  - [ ] Re-scan skills directory after write
- [ ] Action `edit`: full SKILL.md rewrite, same validation + scanning
- [ ] Action `patch`:
  - [ ] Read existing content
  - [ ] Find `OldString` — fail if not found or not unique (unless `ReplaceAll`)
  - [ ] Replace and write atomically
  - [ ] Support `FilePath` parameter for patching resource files
- [ ] Action `delete`: remove directory, clean empty parents, re-scan
- [ ] Action `write_file`:
  - [ ] Validate `FilePath` within `references/`, `scripts/`, `assets/`
  - [ ] Reject traversal and absolute paths
  - [ ] Create subdirectory if needed, write file
- [ ] Action `remove_file`: delete file, clean empty subdirectories
- [ ] All write actions: reject writes to `.system/` directory
- [ ] Inject `ISkillContentScanner` via DI

**Acceptance:** All 6 actions work. Validation rejects bad input. System
skills are read-only. Re-scan triggers after mutations.

### 5. Add slash-command dispatch to SkillRegistry

- [ ] Add `Dictionary<string, SkillEntry> _slashCommands` to `SkillRegistry`
- [ ] Populate during `Register()`: add entry if `UserInvocable != false`
- [ ] Key is skill `Name` (lowercase)
- [ ] Clear on `Clear()`
- [ ] Add `TryResolveSlashCommand(string input, out SkillEntry skill, out string remainder)`:
  - [ ] Strip leading `/` from input
  - [ ] Match against registry keys
  - [ ] Return skill entry and remainder text after the command name
- [ ] Add `GetAvailableSlashCommands()` for error message generation

**Acceptance:** Slash commands resolve by name. Remainder text extracted.
Non-user-invocable skills excluded.

### 6. Add slash-command interception to LlmSessionActor

- [ ] In `SendUserMessage` handling, check if content starts with `/`
- [ ] Call `SkillRegistry.TryResolveSlashCommand()`
- [ ] If matched:
  - [ ] Read SKILL.md body via `SkillScanner.ExtractBody()`
  - [ ] Inject as transient system message (same injection path as auto-loaded
        skills would use)
  - [ ] Pass remainder as user message content
- [ ] If unmatched:
  - [ ] Generate deterministic error listing available commands with argument hints
  - [ ] Send as assistant response — do NOT pass to LLM
- [ ] Verify: works for messages from Slack, webhooks, and scheduled jobs

**Acceptance:** `/netclaw-operations check health` loads skill + passes
remainder. `/nonexistent` returns error with available commands.

### 7. Tests

- [ ] Unit test: `SkillFrontmatter` parses `disable-model-invocation`, `user-invocable`, `argument-hint`
- [ ] Unit test: defaults applied when invocation fields absent
- [ ] Unit test: `skill_load` returns body + resources for known skill
- [ ] Unit test: `skill_load` returns error for unknown skill
- [ ] Unit test: `skill_read_resource` reads valid path
- [ ] Unit test: `skill_read_resource` rejects `..` traversal
- [ ] Unit test: `skill_read_resource` rejects absolute paths
- [ ] Unit test: `skill_read_resource` rejects paths outside standard subdirs
- [ ] Unit test: `skill_manage create` validates frontmatter
- [ ] Unit test: `skill_manage create` rejects invalid name
- [ ] Unit test: `skill_manage patch` replaces unique match
- [ ] Unit test: `skill_manage patch` fails on non-unique match
- [ ] Unit test: `skill_manage delete` removes directory
- [ ] Unit test: `skill_manage` rejects writes to `.system/`
- [ ] Unit test: `skill_manage write_file` validates path
- [ ] Unit test: slash-command dispatch resolves known command
- [ ] Unit test: slash-command dispatch returns error for unknown command
- [ ] Unit test: non-user-invocable skills excluded from slash registry
- [ ] `dotnet slopwatch analyze` — no new violations
