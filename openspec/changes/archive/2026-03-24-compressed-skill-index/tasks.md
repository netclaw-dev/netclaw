## Tasks

### 1. Rewrite GenerateDescriptionMenu to compressed format

- [x] Replace `GenerateDescriptionMenu()` in `SkillRegistry.cs` with
      pipe-delimited format grouped by `Category`
- [x] Include header: `[skills]|load via skill_load(name)|invoke via /name`
- [x] Each category line: `|{category}:{skill-name} — {trigger phrase}`
- [x] Remove absolute file paths and verbose instruction block
- [x] Add `GenerateCompressedMenu(TrustAudience)` internal method
- [x] Parameterless overload returns Personal audience menu (backwards compat)
- [x] Verify: output under 500 tokens for 7 skills

**Acceptance:** Compressed index generated, no `file_read` references, grouped
by category.

### 2. Add audience-aware filtering to menu generation

- [x] Filter skills by trust tier using `DefaultMinimumAudience()` mapping
- [x] Skills with no `AllowedTools` declaration are always visible (no tool gating)
- [x] Add trust-tier visibility filtering:
      All tiers default to Team; External/Agent to Personal; Public sees nothing unless explicitly blessed
- [x] Exclude skills with `DisableModelInvocation = true` from index
- [x] Verify: Public audience does not see any skills (all default to Team min)
- [x] Verify: skill without `allowed-tools` appears in Team and Personal

**Acceptance:** Audience filtering produces different indexes for
Public/Team/Personal. Trust tier visibility enforced.

### 3. Pre-generate per-audience menus

- [x] After skill registry population, generate menus for Public, Team, Personal
- [x] Store as `Dictionary<TrustAudience, string>` in `SkillRegistry`
- [x] Add `GetMenuForAudience(TrustAudience)` method
- [x] Rebuild menus on `Clear()` (clears cached menus) + `RebuildAudienceMenus()`
- [x] Verify: menus cached, no per-turn generation

**Acceptance:** Three pre-built menus available after scan. Menus rebuild
after registry clear + re-populate.

### 4. Make SkillIndexContextLayer audience-aware

- [x] `SkillIndexContextLayer` remains a shared singleton with `Update()` method
- [x] Personal audience menu (most permissive) served as default via `GenerateDescriptionMenu()`
- [x] Per-audience menus available in `SkillRegistry.GetMenuForAudience()` for
      future session-level audience wiring
- [ ] TODO (follow-up): Wire session actor to pass effective audience at context
      layer injection time for full per-session filtering

**Acceptance:** Context layer updated with compressed format. Per-audience
menus available for future session-level wiring.

### 5. Create SkillIndexEnrichmentService

- [x] New `IHostedService` in `src/Netclaw.Daemon/Services/SkillIndexEnrichmentService.cs`
- [x] Runs after `SystemSkillSyncService` completes (registered after it in Program.cs)
- [x] For each skill in registry: check disk cache → LLM sidecar → store result
- [x] Cache path: `~/.netclaw/cache/skill-index/{name}-{version}.json`
- [x] Cache format: `{ "triggerPhrase": "..." }`
- [x] LLM prompt: "Generate a 5-15 word phrase describing when a user would
      need this skill. Use everyday language, not technical jargon."
- [x] Use `ModelRole.Compaction` for cheapest model
- [x] Fallback: first 60 chars of `Description` when sidecar unavailable
- [x] Do NOT cache fallback values (retry on next startup)
- [x] After enrichment: trigger per-audience menu rebuild via `SetTriggerPhrases()`
- [x] Non-blocking: exceptions caught, never blocks startup
- [x] Register in `Program.cs`

**Acceptance:** Trigger phrases generated and cached. Fallback works when
no model available. Menus rebuilt after enrichment.

### 6. Wire trigger phrases into compressed index

- [x] `GenerateCompressedMenu()` reads trigger phrases from `_triggerPhrases` dict
- [x] If no enrichment available yet, uses truncated description (first 60 chars)
- [x] Verify: enriched index contains user-language trigger phrases
- [x] Verify: pre-enrichment index uses truncated descriptions gracefully

**Acceptance:** Compressed index uses LLM-generated trigger phrases when
available, falls back to truncated descriptions.

### 7. Tests

- [x] Unit test: `GenerateDescriptionMenu()` produces pipe-delimited format
- [x] Unit test: trust tier filtering — Public sees nothing, Team sees System/User/Community
- [x] Unit test: `DisableModelInvocation` skills excluded from index
- [x] Unit test: skills without `AllowedTools` always visible (at appropriate audience)
- [x] Unit test: `GetMenuForAudience` returns pre-built menus
- [x] Unit test: enriched trigger phrases used when available
- [x] Unit test: `Clear()` resets audience menus
- [x] All 633 Actors tests + 277 Daemon tests pass
- [x] `dotnet slopwatch analyze` — no new violations
