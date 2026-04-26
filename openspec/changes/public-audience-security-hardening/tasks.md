## 1. AGENTS.md Binary Ownership

- [ ] 1.1 Create `src/Netclaw.Configuration/Resources/AGENTS.md` for Team/Personal
- [ ] 1.2 Create `src/Netclaw.Configuration/Resources/AGENTS.public.md` stripped for Public
- [ ] 1.3 Add both as `<EmbeddedResource>` in `Netclaw.Configuration.csproj`
- [ ] 1.4 Add `TrustAudience audience` parameter to `ISystemPromptProvider.GetSystemPrompt()` and update null/test providers
- [ ] 1.5 Update `FileSystemPromptProvider` to load embedded AGENTS by audience, suppress TOOLING.md and project instructions for Public
- [ ] 1.6 Add runtime placeholder substitution in `FileSystemPromptProvider` using `NetclawPaths`
- [ ] 1.7 Update `LlmSessionActor` to pass resolved audience to `GetSystemPrompt()` at all call sites
- [ ] 1.8 Update onboarding identity file generation to stop writing AGENTS.md (or write reference stub only)
- [ ] 1.9 Unit tests: stripped Public AGENTS, full Team/Personal AGENTS, no TOOLING/project instructions for Public
- [ ] 1.10 Add or update eval coverage for identity/system-prompt changes and run `./evals/run-evals.sh`

## 2. Deployment-Wide Feature Kill Switches

- [ ] 2.1 Add `Enabled` property to `MemoryConfig` (default `true`)
- [ ] 2.2 Add `Enabled` property to `SearchConfig` (default `true`)
- [ ] 2.3 Add `Enabled` property to `SkillSyncConfig` (default `true`)
- [ ] 2.4 Add `Enabled` property to `SubAgentConfig` (default `true`)
- [ ] 2.5 Create new top-level `SchedulingConfig` with only `Enabled` property (default `true`)
- [ ] 2.6 Add `Enabled` to `Webhooks` config (default `true`)
- [ ] 2.7 Update `netclaw-config.v1.schema.json` with all new `Enabled` properties and defaults, including top-level `Scheduling.Enabled`
- [ ] 2.8 Verify `ConfigSchemaDoctorCheck` handles new defaults for existing configs
- [ ] 2.9 Update runtime wiring so disabled subsystems do not register tools/services/watchers/managers at startup

## 3. Feature Selection Wizard Step

- [ ] 3.1 Create `FeatureSelectionStepViewModel` with toggles for memory, search, skills, scheduling, subagents, webhooks
- [ ] 3.2 Show the step only for non-Personal postures
- [ ] 3.3 Write deployment-wide `Enabled` flags in `ContributeConfig()`
- [ ] 3.4 Add `FeatureSelections` to `WizardContext`
- [ ] 3.5 Public defaults: memory/search/skills/scheduling/subagents/webhooks off; Team defaults mostly on
- [ ] 3.6 Create `FeatureSelectionStepView` using the existing checkbox-style TUI pattern
- [ ] 3.7 Register the step after Security Posture
- [ ] 3.8 UI copy clarifies that enabling Search does not implicitly expose `web_search` / `web_fetch` to Public
- [ ] 3.9 Unit tests: posture defaults, config contribution, applicability, Public search note

## 4. Context Layer Audience Threading

- [ ] 4.1 Add `TrustAudience audience` parameter to `IContextLayerProvider.GetContextLayer()`
- [ ] 4.2 Add `TrustAudience Audience` to `ContextAssemblyInput`
- [ ] 4.3 Update `SessionMessageAssembler` to pass audience to all context layer calls
- [ ] 4.4 Update `SkillIndexContextLayer` to return empty for Public or disabled skills
- [ ] 4.5 Update `MemoryIndexContextLayer` to return empty for Public or disabled memory
- [ ] 4.6 Update `SubAgentDiscoveryContextLayer` to return empty for Public or disabled subagents
- [ ] 4.7 Update any other `IContextLayerProvider` implementations found via grep
- [ ] 4.8 Update `LlmSessionActor` to resolve audience and pass it into `ContextAssemblyInput`
- [ ] 4.9 Unit tests: context layers empty when audience/feature gates deny, present when allowed

## 5. Discovery and Load Path Hardening

- [ ] 5.1 Update `search_tools` to hide tools and servers unavailable to the effective audience or disabled feature set
- [ ] 5.2 Update `load_tool` to reject blocked tools with no discovery leakage beyond generic deny/not-found behavior
- [ ] 5.3 Gate `skill_load` and `skill_read_resource` on Public audience and skills runtime exposure
- [ ] 5.4 Gate `spawn_agent` and subagent discovery on Public audience and `SubAgents.Enabled`
- [ ] 5.5 Ensure tool index / skill index / subagent discovery text does not instruct Public to use hidden capabilities
- [ ] 5.6 Unit tests: blocked capabilities absent from discovery results and denied on direct load/spawn paths

## 6. Memory Full Disable and Inert Legacy Public Data

- [ ] 6.1 Remove `store_memory`, `find_memories`, `get_memories`, `update_memory` from the Public audience profile
- [ ] 6.2 Add early return for Public in `SessionRecallManager.ResolveForTurn()`
- [ ] 6.3 Skip memory proposal gate evaluation for Public in `LlmSessionActor`
- [ ] 6.4 Gate recall, explicit search/get, and extraction on `MemoryConfig.Enabled` for all audiences
- [ ] 6.5 Make historical Public-authored memories inert for normal recall/search without adding purge/cleanup code
- [ ] 6.6 Unit tests: Public gets empty recall/search, no extraction; config-disabled suppresses all audiences; legacy Public memories are not surfaced

## 7. Public File Roots and Context Sanitization

- [ ] 7.1 Update file access configuration so Public has no implicit identity, skills, or workspaces roots
- [ ] 7.2 Update `SessionMessageAssembler.BuildStaticContextBlock()` to emit ID-only session block for Public
- [ ] 7.3 Update `SessionMessageAssembler.BuildVolatileContextBlock()` to skip working context for Public
- [ ] 7.4 Update `ScopedFileAccessPolicy` to sanitize error messages for Public
- [ ] 7.5 Unit tests: no implicit internal roots for Public; session block redaction; working context suppression; sanitized errors

## 8. Automatic / Runtime-Owned Behavior and Runtime Wiring

- [ ] 8.1 Gate reminder tools and reminder execution on `Scheduling.Enabled` plus audience allowlists
- [ ] 8.2 Keep background-job shell infrastructure governed by existing shell/background-job policy rather than `Scheduling.Enabled`
- [ ] 8.3 Gate webhook startup/execution on `Webhooks.Enabled`
- [ ] 8.4 Verify autonomous/runtime-owned reminder paths continue using persisted originating audience and do not widen capability exposure after minting
- [ ] 8.5 Unit/integration tests: runtime-disabled prevents reminder startup/registration/execution; audience-blocked sessions cannot use the same feature even when runtime-enabled

## 9. Verification and Docs

- [ ] 9.1 Run `dotnet build`
- [ ] 9.2 Run `dotnet test`
- [ ] 9.3 Run `dotnet slopwatch analyze`
- [ ] 9.4 Run `./evals/run-evals.sh`
- [ ] 9.5 Update system skills if mapped feature areas changed
- [ ] 9.6 Integration test in Docker/containerized Public session covering prompt injection, discovery/load paths, and file-root restrictions
