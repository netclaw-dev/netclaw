## 1. AGENTS.md Binary Ownership

- [x] 1.1 Create `src/Netclaw.Configuration/Resources/AGENTS.md` for Team/Personal
- [x] 1.2 Create `src/Netclaw.Configuration/Resources/AGENTS.public.md` stripped for Public
- [x] 1.3 Add both as `<EmbeddedResource>` in `Netclaw.Configuration.csproj`
- [x] 1.4 Add `TrustAudience audience` parameter to `ISystemPromptProvider.GetSystemPrompt()` and update null/test providers
- [x] 1.5 Update `FileSystemPromptProvider` to load embedded AGENTS by audience, suppress TOOLING.md and project instructions for Public
- [x] 1.6 Add runtime placeholder substitution in `FileSystemPromptProvider` using `NetclawPaths`
- [x] 1.7 Update `LlmSessionActor` to pass resolved audience to `GetSystemPrompt()` at all call sites
- [x] 1.8 Update onboarding identity file generation to stop writing AGENTS.md (or write reference stub only)
- [x] 1.9 Unit tests: stripped Public AGENTS, full Team/Personal AGENTS, no TOOLING/project instructions for Public
- [x] 1.10 Add or update eval coverage for identity/system-prompt changes and run `./evals/run-evals.sh`
- [x] 1.11 Update the embedded Public AGENTS attachment wording so it matches the redacted/pathless Public session block and does not mention `session_dir`, `media_dir`, or `inbox/`

## 2. Deployment-Wide Feature Kill Switches

- [x] 2.1 Add `Enabled` property to `MemoryConfig` (default `true`)
- [x] 2.2 Add `Enabled` property to `SearchConfig` (default `true`)
- [x] 2.3 Add `Enabled` property to `SkillSyncConfig` (default `true`)
- [x] 2.4 Add `Enabled` property to `SubAgentConfig` (default `true`)
- [x] 2.5 Create new top-level `SchedulingConfig` with only `Enabled` property (default `true`)
- [x] 2.6 Add `Enabled` to `Webhooks` config (default `true`)
- [x] 2.7 Update `netclaw-config.v1.schema.json` with all new `Enabled` properties and defaults, including top-level `Scheduling.Enabled`
- [x] 2.8 Verify `ConfigSchemaDoctorCheck` handles new defaults for existing configs
- [x] 2.9 Update runtime wiring so disabled subsystems do not register tools/services/watchers/managers at startup

## 3. Feature Selection Wizard Step

- [x] 3.1 Create `FeatureSelectionStepViewModel` with toggles for memory, search, skills, scheduling, subagents, webhooks
- [x] 3.2 Show the step only for non-Personal postures
- [x] 3.3 Write deployment-wide `Enabled` flags in `ContributeConfig()`
- [x] 3.4 Add `FeatureSelections` to `WizardContext`
- [x] 3.5 Public defaults: memory/search/skills/scheduling/subagents/webhooks off; Team defaults mostly on
- [x] 3.6 Create `FeatureSelectionStepView` using the existing checkbox-style TUI pattern
- [x] 3.7 Register the step after Security Posture
- [x] 3.8 UI copy clarifies that enabling Search does not implicitly expose `web_search` / `web_fetch` to Public
- [x] 3.9 Unit tests: posture defaults, config contribution, applicability, Public search note

## 4. Context Layer Audience Threading

- [x] 4.1 Add `TrustAudience audience` parameter to `IContextLayerProvider.GetContextLayer()`
- [x] 4.2 Add `TrustAudience Audience` to `ContextAssemblyInput`
- [x] 4.3 Update `SessionMessageAssembler` to pass audience to all context layer calls
- [x] 4.4 Update `SkillIndexContextLayer` to return empty for Public or disabled skills
- [x] 4.5 Update `MemoryIndexContextLayer` to return empty for Public or disabled memory
- [x] 4.6 Update `SubAgentDiscoveryContextLayer` to return empty for Public or disabled subagents
- [x] 4.7 Update any other `IContextLayerProvider` implementations found via grep
- [x] 4.8 Update `LlmSessionActor` to resolve audience and pass it into `ContextAssemblyInput`
- [x] 4.9 Unit tests: context layers empty when audience/feature gates deny, present when allowed
- [x] 4.10 Fix Slack/Discord session-start audience threading so the initial `GetSystemPrompt()` call and startup context/tool index use the resolved channel audience on the first turn

## 5. Discovery and Load Path Hardening

- [x] 5.1 Update `search_tools` to hide tools and servers unavailable to the effective audience or disabled feature set
- [x] 5.2 Update `load_tool` to reject blocked tools with no discovery leakage beyond generic deny/not-found behavior
- [x] 5.3 Gate `skill_load` and `skill_read_resource` on Public audience and skills runtime exposure
- [x] 5.4 Gate `spawn_agent` and subagent discovery on Public audience and `SubAgents.Enabled`
- [x] 5.5 Ensure tool index / skill index / subagent discovery text does not instruct Public to use hidden capabilities
- [x] 5.6 Unit tests: blocked capabilities absent from discovery results and denied on direct load/spawn paths
- [x] 5.7 Ensure the initial startup tool index/context for Public sessions also omits hidden capabilities before any later refresh/rebuild occurs

## 6. Memory Full Disable and Legacy Public Data Handling

- [x] 6.1 Remove `store_memory`, `find_memories`, `get_memories`, `update_memory` from the Public audience profile
- [x] 6.2 Add early return for Public in `SessionRecallManager.ResolveForTurn()`
- [x] 6.3 Skip memory proposal gate evaluation for Public in `LlmSessionActor`
- [x] 6.4 Gate recall, explicit search/get, and extraction on `MemoryConfig.Enabled` for all audiences
- [x] 6.5 Align legacy Public-memory handling with the clarified contract: Public sessions cannot write memories or perform recall/search, but trusted higher-privilege contexts do not automatically suppress historical Public-authored memories
- [x] 6.6 Unit tests: Public gets no memory writes/recall/search and no extraction; config-disabled suppresses all audiences; Team/Personal trusted paths may still surface or manage historical Public-authored memories under normal policy

## 7. Public File Roots and Context Sanitization

- [x] 7.1 Update file access configuration so Public has no implicit identity, skills, or workspaces roots
- [x] 7.2 Update `SessionMessageAssembler.BuildStaticContextBlock()` to emit ID-only session block for Public
- [x] 7.3 Update `SessionMessageAssembler.BuildVolatileContextBlock()` to skip working context for Public
- [x] 7.4 Update `ScopedFileAccessPolicy` to sanitize error messages for Public
- [x] 7.5 Unit tests: no implicit internal roots for Public; session block redaction; working context suppression; sanitized errors
- [x] 7.6 Remove any Public denial wording that names or implies allowed roots, including the session directory

## 8. Automatic / Runtime-Owned Behavior and Runtime Wiring

- [x] 8.1 Gate reminder tools and reminder execution on `Scheduling.Enabled` plus audience allowlists
- [x] 8.2 Keep background-job shell infrastructure governed by existing shell/background-job policy rather than `Scheduling.Enabled`
- [x] 8.3 Gate webhook startup/execution on `Webhooks.Enabled`
- [x] 8.4 Verify autonomous/runtime-owned reminder paths continue using persisted originating audience and do not widen capability exposure after minting
- [x] 8.5 Unit/integration tests: runtime-disabled prevents reminder startup/registration/execution; audience-blocked sessions cannot use the same feature even when runtime-enabled

## 9. Verification and Docs

- [x] 9.1 Run `dotnet build`
- [x] 9.2 Run `dotnet test`
- [x] 9.3 Run `dotnet slopwatch analyze`
- [x] 9.4 Run `./evals/run-evals.sh`
- [x] 9.5 Update system skills if mapped feature areas changed
- [x] 9.6 Integration test in Docker/containerized Public session covering prompt injection, discovery/load paths, and file-root restrictions
