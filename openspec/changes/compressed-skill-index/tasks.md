## Tasks

### 1. Rewrite GenerateDescriptionMenu to compressed format

- [ ] Replace `GenerateDescriptionMenu()` in `SkillRegistry.cs` with
      pipe-delimited format grouped by `Category`
- [ ] Include header: `[skills]|load via skill_load(name)|invoke via /name`
- [ ] Each category line: `|{category}:{skill-name} — {trigger phrase}`
- [ ] Remove absolute file paths and verbose instruction block
- [ ] Add `GenerateDescriptionMenu(TrustAudience, IReadOnlySet<string>)` overload
- [ ] Parameterless overload calls new overload with `Personal` + all tools (backwards compat)
- [ ] Verify: output under 500 tokens for 7 skills

**Acceptance:** Compressed index generated, no `file_read` references, grouped
by category.

### 2. Add audience-aware filtering to menu generation

- [ ] In the filtered overload, exclude skills whose `AllowedTools` contains
      tools not in the `availableTools` set
- [ ] Skip skills with no `AllowedTools` declaration (always visible)
- [ ] Add trust-tier visibility filtering:
      System/Operator → all, Community → Team+Personal, External/Agent → Personal
- [ ] Exclude skills with `DisableModelInvocation = true` from index
- [ ] Verify: Public audience does not see skills requiring `shell_execute`
- [ ] Verify: skill without `allowed-tools` appears in all audiences

**Acceptance:** Audience filtering produces different indexes for
Public/Team/Personal. Skill requiring shell hidden from Public.

### 3. Pre-generate per-audience menus

- [ ] After skill registry population, generate menus for Public, Team, Personal
- [ ] Store as `Dictionary<TrustAudience, string>` in `SkillRegistry`
- [ ] Add `GetMenuForAudience(TrustAudience)` method
- [ ] Rebuild menus on `Clear()` + re-population (feed sync, skill_manage mutation)
- [ ] Verify: menus cached, no per-turn generation

**Acceptance:** Three pre-built menus available after scan. Menus rebuild
after registry clear + re-populate.

### 4. Make SkillIndexContextLayer audience-aware

- [ ] Modify `SkillIndexContextLayer` to accept audience context at injection time
- [ ] Select pre-built menu from `SkillRegistry.GetMenuForAudience(audience)`
- [ ] Update `LlmSessionActor` to pass effective audience when assembling
      context layers (or when initializing the session's context layer snapshot)
- [ ] Verify: Team session gets Team-filtered menu in system prompt

**Acceptance:** Different sessions with different audiences get different
skill indexes in their system prompts.

### 5. Create SkillIndexEnrichmentService

- [ ] New `IHostedService` in `src/Netclaw.Daemon/Services/SkillIndexEnrichmentService.cs`
- [ ] Runs after `SystemSkillSyncService` completes (register after it in Program.cs)
- [ ] For each skill in registry: check disk cache → LLM sidecar → store result
- [ ] Cache path: `~/.netclaw/cache/skill-index/{name}-{version}.json`
- [ ] Cache format: `{ "triggerPhrase": "..." }`
- [ ] LLM prompt: "Generate a 5-15 word phrase describing when a user would
      need this skill. Use everyday language, not technical jargon."
- [ ] Use `ModelRole.Compaction` for cheapest model
- [ ] Fallback: first 60 chars of `Description` when sidecar unavailable
- [ ] Do NOT cache fallback values (retry on next startup)
- [ ] After enrichment: trigger per-audience menu rebuild in `SkillRegistry`
- [ ] Non-blocking: never block daemon startup or session creation
- [ ] Register in `Program.cs`

**Acceptance:** Trigger phrases generated and cached. Fallback works when
no model available. Menus rebuilt after enrichment.

### 6. Wire trigger phrases into compressed index

- [ ] `GenerateDescriptionMenu()` reads trigger phrases from enrichment results
- [ ] If no enrichment available yet (service still running), use truncated description
- [ ] Verify: enriched index contains user-language trigger phrases
- [ ] Verify: pre-enrichment index uses truncated descriptions gracefully

**Acceptance:** Compressed index uses LLM-generated trigger phrases when
available, falls back to truncated descriptions.

### 7. Tests

- [ ] Unit test: `GenerateDescriptionMenu()` produces pipe-delimited format
- [ ] Unit test: audience filtering excludes skills by `AllowedTools`
- [ ] Unit test: trust tier filtering — Community hidden from Public, visible to Team
- [ ] Unit test: `DisableModelInvocation` skills excluded from index
- [ ] Unit test: skills without `AllowedTools` always visible
- [ ] Unit test: `GetMenuForAudience` returns pre-built menus
- [ ] Integration test: `SkillIndexEnrichmentService` caches trigger phrases
- [ ] Integration test: enrichment fallback when sidecar unavailable
- [ ] `dotnet slopwatch analyze` — no new violations
