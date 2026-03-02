# Tasks: unified-memory-provider

## Milestone 1: Core memory provider abstraction and file backend

### Task 1.1: Add IMemoryProvider interface and config schema
- [ ] Create `IMemoryProvider` interface in `Netclaw.Actors` with `SearchAsync` and `StoreAsync`
- [ ] Add `Memory` config section to `Netclaw.Configuration` (parsed from `netclaw.json`)
- [ ] Add `MemoriesDirectory` to `NetclawPaths`
- [ ] Ensure `~/.netclaw/memories/` is created at startup alongside other directories

**Acceptance:** Config section parses correctly with `"files"` and `"memorizer"` values. `NetclawPaths.MemoriesDirectory` resolves to `~/.netclaw/memories/`.

### Task 1.2: Implement FileMemoryProvider
- [ ] Create `FileMemoryProvider` that stores memories as individual `.md` files in `~/.netclaw/memories/`
- [ ] File naming: `{date}-{kebab-title}.md` with YAML front matter (title, tags, created date)
- [ ] Implement `memory.md` index file — markdown table listing all memories with title, tags, file path
- [ ] `StoreAsync`: write memory file, update index
- [ ] `SearchAsync`: substring match against index entries and file content, return top N results formatted as readable text
- [ ] Rebuild index from files on startup (handle manual edits/deletions)

**Acceptance:** Store creates file + updates index. Search finds memories by title, tag, or content substring. Index stays consistent after manual file additions/deletions.

### Task 1.3: Add StoreMemoryTool as always-loaded builtin
- [ ] Create `StoreMemoryTool` in `Netclaw.Actors.Memory` following `SearchMemoriesTool` patterns
- [ ] Parameters: `Title` (required), `Content` (required), `Tags` (optional string array)
- [ ] Delegates to `IMemoryProvider.StoreAsync`
- [ ] Register as always-loaded builtin (not MCP, not requiring discovery)
- [ ] Grant category: `builtin`

**Acceptance:** `StoreMemoryTool` appears in `GetAlwaysLoadedTools()`. Agent can call `store_memory` without prior `search_tools` call. Tool works against file backend.

### Task 1.4: Refactor SearchMemoriesTool to use IMemoryProvider
- [ ] Replace hardcoded `memorizer/search_memories` MCP lookup with `IMemoryProvider.SearchAsync`
- [ ] Remove `MemorizerToolNames` constant and `FindMemorizerTool` method
- [ ] Keep graceful error when provider is unavailable
- [ ] Update existing tests

**Acceptance:** `SearchMemoriesTool` delegates to `IMemoryProvider` instead of directly resolving MCP tools. Existing tests updated and passing. File backend works end-to-end.

### Task 1.5: Wire IMemoryProvider and IMemoryExtractor in DI
- [ ] In `Program.cs`, read `Memory.Provider` config value (default: `"files"`)
- [ ] Register `FileMemoryProvider` or `MemorizerMemoryProvider` based on config
- [ ] Create `ProviderMemoryExtractor` that delegates to `IMemoryProvider.StoreAsync`
- [ ] Register `ProviderMemoryExtractor` as `IMemoryExtractor` (replacing `NullMemoryExtractor`)
- [ ] Inject `IMemoryProvider` into `SearchMemoriesTool` and `StoreMemoryTool`

**Acceptance:** Pre-compaction memory extraction fires and persists to the active backend. `NullMemoryExtractor` is no longer the default when a provider is configured.

### Task 1.6: Unit tests for file backend and tools
- [ ] `FileMemoryProviderTests`: store, search, index rebuild, concurrent writes
- [ ] `StoreMemoryToolTests`: parameter validation, delegation to provider, grant category
- [ ] Update `SearchMemoriesToolTests` for provider-based delegation
- [ ] `ProviderMemoryExtractorTests`: verify extraction routes to provider

**Acceptance:** All tests pass. Coverage on store, search, index management, and extraction wiring.

---

## Milestone 2: Memorizer backend and provider-aware context layer

### Task 2.1: Implement MemorizerMemoryProvider
- [ ] Create `MemorizerMemoryProvider` that delegates to MCP tools via `ToolRegistry`
- [ ] `SearchAsync` → resolve and call `memorizer/search_memories`
- [ ] `StoreAsync` → resolve and call `memorizer/store`
- [ ] Graceful error when Memorizer MCP is not connected

**Acceptance:** When Memorizer MCP is connected, `search_memories` and `store_memory` delegate to it. When disconnected, clear error message returned.

### Task 2.2: Update MemoryIndexContextLayer for provider-aware content
- [ ] Accept provider type in `Update` method (not just connected boolean)
- [ ] File backend: show RETRIEVE/SAVE rules pointing to `search_memories`, `store_memory`, and `memory.md` index
- [ ] Memorizer backend (connected): show RETRIEVE/SAVE rules, explain two-step discovery for advanced ops, reference `memorizer-usage` skill
- [ ] Memorizer backend (disconnected): show unavailable message with troubleshooting guidance
- [ ] No backend configured: show fallback to identity files

**Acceptance:** Context layer content varies by provider and connection state. Integration test confirms agent sees correct guidance for each scenario.

### Task 2.3: Update memorizer-usage skill with discovery workflow
- [ ] Add section explaining the two-step pattern: `search_memories`/`store_memory` are direct, everything else needs `search_tools` first
- [ ] Add graph traversal workflow: find memory → see projectId → discover project tools → explore project → find related memories
- [ ] Clarify which tools are always-available vs. discovery-required

**Acceptance:** Skill file clearly documents the two tiers of memory operations.

### Task 2.4: Tests for Memorizer backend and context layer
- [ ] `MemorizerMemoryProviderTests`: delegation to MCP tools, disconnected fallback
- [ ] Update `MemoryIndexContextLayer` tests for provider-aware content (file, memorizer-connected, memorizer-disconnected, no-backend)

**Acceptance:** All tests pass for both backends and all context layer states.

---

## Milestone 3: Onboarding, diagnostics, and status

### Task 3.1: Add memory wizard step to netclaw init
- [ ] Add `WizardStep.Memory` between `BrowserAutomation` and `Exposure`
- [ ] Selection: "File-based memory (recommended)" or "Memorizer (MCP server)"
- [ ] File-based: no further input, writes `Memory.Provider = "files"` to config
- [ ] Memorizer: prompt for endpoint URL (default `http://localhost:5012/mcp`), write both `Memory` config and `McpServers` entry
- [ ] Update `TotalSteps` constant

**Acceptance:** Wizard includes memory step. Both paths produce valid configuration. Back-navigation clears memory config.

### Task 3.2: Add memory doctor check
- [ ] Add `MemoryDoctorCheck` to `Netclaw.Cli/Doctor/`
- [ ] File backend: verify `~/.netclaw/memories/` exists and is writable
- [ ] Memorizer backend: verify MCP server entry exists in config, verify MCP server is connected (via daemon status API)
- [ ] Report pass/fail with remediation guidance

**Acceptance:** `netclaw doctor` includes memory check. Clear pass/fail output for each backend type.

### Task 3.3: Add memory to netclaw status output
- [ ] Add `memory:` line to status output
- [ ] Show provider name, health status, and backend-specific details
- [ ] File backend: memory count, index path
- [ ] Memorizer: endpoint, tool count, connection status

**Acceptance:** `netclaw status` shows memory provider info alongside existing model/connector output.

### Task 3.4: Integration test — end-to-end memory workflow
- [ ] Start daemon with file backend, run headless prompt that triggers search + store
- [ ] Verify memory file created in `~/.netclaw/memories/` with correct content
- [ ] Verify `memory.md` index updated
- [ ] Verify subsequent search finds the stored memory

**Acceptance:** Full round-trip: store → index update → search retrieval, all via CLI headless mode.

---

## Milestone 4: Spec reconciliation

### Task 4.1: Sync delta specs to main specs
- [ ] Sync `netclaw-agent-memory` delta spec to `openspec/specs/netclaw-agent-memory/spec.md`
- [ ] Sync `netclaw-onboarding` delta spec to `openspec/specs/netclaw-onboarding/spec.md`
- [ ] Sync `netclaw-cli` delta spec to `openspec/specs/netclaw-cli/spec.md`

**Acceptance:** Main specs reflect reconciled requirements. No stale references to removed features or renamed files.

### Task 4.2: Update ADR-002
- [ ] Update ADR-002 to reflect unified memory provider architecture
- [ ] Document file-based backend as default, Memorizer as upgrade path
- [ ] Document `IMemoryProvider` abstraction and `IMemoryExtractor` wiring

**Acceptance:** ADR-002 accurately describes the current memory architecture.
