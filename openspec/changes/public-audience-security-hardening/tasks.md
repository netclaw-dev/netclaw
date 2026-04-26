## 1. AGENTS.md Binary Ownership

- [ ] 1.1 Create `src/Netclaw.Configuration/Resources/AGENTS.md` — full audience variant from current AGENTS.template.md with `{{placeholder}}` syntax
- [ ] 1.2 Create `src/Netclaw.Configuration/Resources/AGENTS.public.md` — stripped variant (Autonomy Rules, Grounding Rules, Search Decision Rules only)
- [ ] 1.3 Add both as `<EmbeddedResource>` in `Netclaw.Configuration.csproj`
- [ ] 1.4 Add `TrustAudience audience` parameter to `ISystemPromptProvider.GetSystemPrompt()` and update `NullSystemPromptProvider`
- [ ] 1.5 Update `FileSystemPromptProvider` to load AGENTS from embedded resource by audience, suppress TOOLING.md and project instructions for Public
- [ ] 1.6 Add runtime placeholder substitution in `FileSystemPromptProvider` using `NetclawPaths` values
- [ ] 1.7 Update `LlmSessionActor` to pass resolved audience to `GetSystemPrompt()` at all call sites (~line 245, ~652, ~2274)
- [ ] 1.8 Update `IdentityStepViewModel.WriteIdentityFiles()` to stop writing AGENTS.md (or write reference stub)
- [ ] 1.9 Unit tests: `FileSystemPromptProvider` returns stripped AGENTS for Public, full for Team/Personal, no TOOLING for Public

## 2. Config Schema and Enabled Flags

- [ ] 2.1 Add `Enabled` property to `MemoryConfig` (default `true`)
- [ ] 2.2 Add `Enabled` property to `SubAgentConfig` (default `true`)
- [ ] 2.3 Create or update scheduling config with `Enabled` property
- [ ] 2.4 Add `Enabled` to `SkillSync` section config
- [ ] 2.5 Add `Enabled` to `Webhooks` section config
- [ ] 2.6 Update `netclaw-config.v1.schema.json` with all new `Enabled` properties and defaults
- [ ] 2.7 Verify `ConfigSchemaDoctorCheck` handles new properties with defaults for existing configs

## 3. Feature Selection Wizard Step

- [ ] 3.1 Create `FeatureSelectionStepViewModel` — checkbox toggles for memory, skills, scheduling, subagents, webhooks, web search
- [ ] 3.2 Implement `IsApplicable()` to show only for non-Personal postures
- [ ] 3.3 Implement `ContributeConfig()` to write `Enabled` flags based on selections
- [ ] 3.4 Add `FeatureSelections` property to `WizardContext` for downstream steps
- [ ] 3.5 Pre-populate defaults based on `SelectedPosture` (Public: most off, Team: most on)
- [ ] 3.6 Create `FeatureSelectionStepView` — checkbox-style TUI using ExternalSkillsStepView pattern
- [ ] 3.7 Register step in wizard step list after SecurityPosture, before Channels
- [ ] 3.8 Unit tests: verify defaults per posture, config contribution, IsApplicable logic

## 4. Context Layer Audience Threading

- [ ] 4.1 Add `TrustAudience audience` parameter to `IContextLayerProvider.GetContextLayer()`
- [ ] 4.2 Add `TrustAudience Audience` field to `ContextAssemblyInput` record
- [ ] 4.3 Update `SessionMessageAssembler` to pass audience to `GetContextLayer()` calls
- [ ] 4.4 Update `SkillIndexContextLayer` to return empty for Public
- [ ] 4.5 Update `MemoryIndexContextLayer` to return empty for Public or when Memory disabled
- [ ] 4.6 Update `SubAgentDiscoveryContextLayer` to return empty for Public
- [ ] 4.7 Update any other `IContextLayerProvider` implementations found via grep
- [ ] 4.8 Update `LlmSessionActor` to resolve audience and pass to `ContextAssemblyInput`
- [ ] 4.9 Unit tests: each context layer returns empty for Public, full for Team/Personal

## 5. Memory Full Disable

- [ ] 5.1 Remove `store_memory`, `find_memories`, `get_memories`, `update_memory` from Public profile in `ToolAudienceProfiles`
- [ ] 5.2 Add early return for Public audience in `SessionRecallManager.ResolveForTurn()`
- [ ] 5.3 Skip memory proposal gate evaluation for Public audience in `LlmSessionActor`
- [ ] 5.4 Gate recall and extraction on `MemoryConfig.Enabled` for all audiences
- [ ] 5.5 Unit tests: Public gets empty recall, no extraction; config-disabled suppresses for all audiences

## 6. Session Block and Error Sanitization

- [ ] 6.1 Update `SessionMessageAssembler.BuildStaticContextBlock()` to emit ID-only session block for Public
- [ ] 6.2 Update `SessionMessageAssembler.BuildVolatileContextBlock()` to skip working context for Public
- [ ] 6.3 Update `ScopedFileAccessPolicy` to sanitize error messages for Public (omit root paths)
- [ ] 6.4 Unit tests: session block redaction, working context suppression, error sanitization

## 7. Verification and Docs

- [ ] 7.1 Run `dotnet build` and fix any compilation errors
- [ ] 7.2 Run `dotnet test` and fix any test failures
- [ ] 7.3 Run `dotnet slopwatch analyze` and fix any new violations
- [ ] 7.4 Update system skills if mapped feature areas changed (see CLAUDE.md table)
- [ ] 7.5 Integration test in Docker container with public audience Discord session
