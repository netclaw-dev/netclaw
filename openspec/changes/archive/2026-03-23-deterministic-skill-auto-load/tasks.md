## 1. Shared TextTokenizer (DRY refactoring)

- [x] 1.1 Create `src/Netclaw.Actors/Text/TextTokenizer.cs` with `Tokenize()`, `MakeBigrams()`, `NormalizePlural()`, and the shared stopword set — extracted from `DeterministicRetrievalPlanning.cs` lines 25-149
- [x] 1.2 Update `DeterministicRetrievalPlanning.cs` to use `TextTokenizer` instead of its private tokenizer/stopwords copy
- [x] 1.3 Update `DeterministicCandidateSelector.cs` to use `TextTokenizer` instead of its private tokenizer copy
- [x] 1.4 Create `src/Netclaw.Actors.Tests/Text/TextTokenizerTests.cs` — tokenization, stopword removal, case normalization, plural normalization (prices→price, categories→category, matches→match, class stays class)
- [x] 1.5 Run full `Netclaw.Actors.Tests` suite to verify DRY refactor introduces no regressions

## 2. Enriched keyword index in SkillRegistry

- [x] 2.1 Add `Dictionary<string, HashSet<string>> _enrichedKeywords` to `SkillRegistry` with `SetEnrichedKeywords()` and `GetEnrichedKeywords()` methods
- [x] 2.2 Ensure `SkillRegistry.Clear()` also clears the enriched keyword index
- [x] 2.3 Add `MatchByKeywords(string userMessage, IReadOnlySet<string>? excludeNames, int threshold, int maxResults)` method using `TextTokenizer` for token-level set intersection scoring
- [x] 2.4 Create `src/Netclaw.Actors.Tests/Skills/SkillRegistryMatchTests.cs` — threshold behavior (score >= 2 matches, score 1 doesn't), exclude set, max results cap, score-descending sort, empty/null message, skills without enriched keywords skipped

## 3. LLM sidecar keyword enrichment service

- [x] 3.1 Add `CacheDirectory` and `SkillKeywordCacheDirectory` properties to `NetclawPaths`
- [x] 3.2 Create `src/Netclaw.Daemon/Services/SkillTriggerEnrichmentService.cs` as `IHostedService` — inject `IChatClientProvider`, `SkillRegistry`, `NetclawPaths`, `ILogger`
- [x] 3.3 Implement per-skill enrichment loop: check cache (name + version + content hash) → call sidecar LLM via `SessionSidecarRunner.RunJsonAsync` with `ModelRole.Compaction` → parse keywords → cache to disk → store in registry
- [x] 3.4 Implement degradation fallback: if sidecar fails, tokenize skill's `Triggers` + `Description` as basic keyword set
- [x] 3.5 Register `SkillTriggerEnrichmentService` in `Program.cs` after `SystemSkillSyncService`
- [x] 3.6 Wire `SystemSkillSyncService` to trigger re-enrichment after feed sync updates skill files

## 4. Session actor integration

- [x] 4.1 Add `SkillRegistry? skillRegistry` constructor parameter to `LlmSessionActor` (after `ToolRegistry`)
- [x] 4.2 Add transient fields: `HashSet<string> _autoLoadedSkills` and `Dictionary<string, string> _autoLoadedSkillContent`
- [x] 4.3 Implement `ResolveAndInjectAutoLoadedSkills()` — call `MatchByKeywords` for new matches, read SKILL.md for new skills (try/catch IOException), inject ALL loaded skills from cache as tagged transient system message
- [x] 4.4 Integrate into `FireLlmCall()` between `InjectAutomaticRecall` (line 1342) and `InjectDynamicContextLayers` (line 1346)
- [x] 4.5 Clear `_autoLoadedSkills` and `_autoLoadedSkillContent` in the `SessionCompacted` persist callback (line 627)
- [x] 4.6 Add structured log: `turn_skill_auto_load new={New} total={Total} skills={Names} scores={Scores}`

## 5. Verification

- [x] 5.1 Run `dotnet build` — full solution compiles
- [x] 5.2 Run `dotnet test src/Netclaw.Actors.Tests/` — all tests pass including new ones
- [x] 5.3 Run `dotnet slopwatch analyze` — no new violations
- [ ] 5.4 Build and swap daemon binary, run `netclaw -p`, send "I need to buy a new 2-keg CO2 regulator for my kegorator" — verify `turn_skill_auto_load` appears in daemon log and response includes source URLs
- [ ] 5.5 Send an ambiguous message like "What do I know about CO2 regulators?" — verify NO skill auto-load occurs (below threshold)
