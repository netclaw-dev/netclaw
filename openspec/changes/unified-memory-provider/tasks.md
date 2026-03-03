# Tasks: unified-memory-provider

> **Design divergence note:** The implementation diverged from the original
> design. Instead of an `IMemoryProvider` abstraction with 2 tools
> (`search_memories`, `store_memory`), we built 4 dedicated tool classes per
> backend (`find_memories`, `get_memories`, `store_memory`, `update_memory`)
> with no shared abstraction. Memorizer tools resolve MCP at call time;
> `store_memory` uses a subagent. See `operationalize-subagent-core` (archived)
> for the subagent infrastructure.

## Milestone 1: Core memory provider abstraction and file backend

### Task 1.1: Add memory config and paths
- [x] Add `MemoryConfig` class in `Netclaw.Configuration` with `Provider` property (default `"files"`)
- [x] Add `MemoriesDirectory` to `NetclawPaths`
- [x] Ensure `~/.netclaw/memories/` is created at startup alongside other directories

> _Design divergence: No `IMemoryProvider` interface — each backend has its own
> tool implementations. Config schema and paths match spec._

### Task 1.2: Implement FileMemoryStore
- [x] Create `FileMemoryStore` that stores memories as individual `.md` files in `~/.netclaw/memories/`
- [x] File naming: `{date}-{kebab-title}.md` with YAML front matter (title, tags, created date)
- [x] Implement `memory.md` index file — markdown table listing all memories
- [x] `StoreAsync`: write memory file, update index
- [x] `SearchAsync`: substring match with scoring (title=3, tag=2, content=1)
- [x] `GetByIdsAsync`: fetch full content by file ID
- [x] `EditAsync`: find-and-replace within memory files
- [x] `DeleteAsync`: hard delete memory files, update index
- [x] Thread-safe via `SemaphoreSlim`, in-memory cache invalidated on writes

> _Design divergence: Named `FileMemoryStore` (not `FileMemoryProvider`). Added
> `GetByIdsAsync`, `EditAsync`, `DeleteAsync` for 4-tool surface. Scoring is
> multi-level (not simple substring)._

### Task 1.3: Add StoreMemoryTool as always-loaded builtin
- [x] Create `StoreMemoryTool` in `Netclaw.Actors.Memory`
- [x] Parameters: `Title` (required), `Content` (required), `Tags` (optional)
- [x] Delegates to `FileMemoryStore.StoreAsync`
- [x] Register as always-loaded builtin

### Task 1.4: Implement 4-tool file-backed surface
- [x] `FileFindMemoriesTool` — lightweight results (ID, title, score, tags, snippet)
- [x] `FileGetMemoriesTool` — full content by IDs
- [x] `StoreMemoryTool` — create new memories
- [x] `FileUpdateMemoryTool` — edit (find-replace) and delete modes

> _Design divergence: Replaced `SearchMemoriesTool` refactoring with new
> `FileFindMemoriesTool`. Added `FileGetMemoriesTool` and `FileUpdateMemoryTool`
> (not in original spec). Two-phase retrieval pattern: find → get._

### Task 1.5: Wire memory tools and extractors in DI
- [x] Read `Memory.Provider` config value in `Program.cs` (default: `"files"`)
- [x] Register `FileMemoryExtractor` as `IMemoryExtractor` for file backend
- [x] Register `MemorizerMemoryExtractor` as `IMemoryExtractor` for memorizer backend
- [x] `ToolIndexUpdater` wires correct backend tools based on config + MCP state

> _Design divergence: No `ProviderMemoryExtractor` — separate extractors per
> backend. `ToolIndexUpdater` handles dynamic tool registration after MCP
> discovery rather than static DI._

### Task 1.6: Unit tests for file backend and tools
- [x] `FileMemoryStore` tests: store, search, get-by-ids, edit, delete, index rebuild
- [x] `StoreMemoryTool` tests
- [x] `FileFindMemoriesTool` tests
- [x] `FileGetMemoriesTool` tests
- [x] `FileUpdateMemoryTool` tests
- [x] `FileMemoryExtractor` tests

---

## Milestone 2: Memorizer backend and provider-aware context layer

### Task 2.1: Implement Memorizer-backed memory tools
- [x] `MemorizerFindMemoriesTool` — pass-through to `memorizer/search_memories` MCP
- [x] `MemorizerGetMemoriesTool` — pass-through to `memorizer/get_many` MCP
- [x] `MemorizerStoreMemoryTool` — spawns `memory-curator` subagent with 8 MCP tools
- [x] `MemorizerUpdateMemoryTool` — edit via `memorizer/edit`, delete via `memorizer/archive_memory`
- [x] All tools resolve MCP at call time, graceful error when disconnected

> _Design divergence: No `MemorizerMemoryProvider`. 4 separate tool classes
> instead of provider abstraction. `store_memory` uses subagent delegation
> (from `operationalize-subagent-core`). Other tools are direct MCP pass-throughs._

### Task 2.2: Update MemoryIndexContextLayer for provider-aware content
- [x] Three states: `FileBacked`, `MemorizerConnected`, `MemorizerDisconnected`
- [x] File backend: 4-tool guidance, memory index reference, quality bar
- [x] Memorizer connected: 4-tool guidance, subagent delegation note, latency warning
- [x] Memorizer disconnected: troubleshooting guidance
- [x] Updated by `ToolIndexUpdater.StartAsync()` via `Update(MemoryContextState)`

### Task 2.3: Update memorizer-usage skill
- [x] Updated to v1.1.0 with subagent delegation note, two-tier model, advanced ops
- [x] Embedded copy at `src/Netclaw.Daemon/BuiltInSkills/memorizer-usage.md` in sync

### Task 2.4: Tests for Memorizer backend and context layer
- [x] `MemorizerFindMemoriesToolTests`: MCP delegation, disconnected fallback
- [x] `MemorizerGetMemoriesToolTests`: MCP delegation, disconnected fallback
- [x] `MemorizerStoreMemoryToolTests`: subagent spawning, timeout, disconnected fallback
- [x] `MemorizerUpdateMemoryToolTests`: edit/delete modes, disconnected fallback
- [x] `MemoryIndexContextLayer` tests for all three states

---

## Milestone 3: Onboarding, diagnostics, and status

### Task 3.1: Add memory wizard step to netclaw init
- [x] Added `WizardStep.Memory = 6`, shifted Exposure=7, Identity=8, HealthCheck=9
- [x] Selection: "Local files (recommended)" or "Memorizer (MCP server)"
- [x] File-based: writes `Memory.Provider = "files"`, Memorizer: substeps for transport/connection
- [x] Memorizer connectivity probe with 10s timeout, fallback to files
- [x] Health check reports degraded (not failed) when Memorizer unreachable
- [x] `TotalSteps = 9`

### ~~Task 3.2: Add memory doctor check~~ (dropped — low value)

> Dropped: file backend auto-creates its directory, and Memorizer connectivity
> is already covered by `McpServersDoctorCheck`. The only gap (config
> consistency: `Memory.Provider=memorizer` without a `McpServers.memorizer`
> entry) can be a schema validation rule if needed.

### Task 3.3: Add memory to netclaw status output
- [ ] Add `memory:` line to status output
- [ ] Show provider name, health status, and backend-specific details

### Task 3.4: Integration test — end-to-end memory workflow
- [ ] Start daemon with file backend, run headless prompt that triggers search + store
- [ ] Verify memory file created with correct content
- [ ] Verify `memory.md` index updated
- [ ] Verify subsequent search finds the stored memory

---

## Milestone 4: Spec reconciliation

### Task 4.1: Sync delta specs to main specs
- [x] Sync `netclaw-agent-memory` delta spec (reconciled identity paths + 4-tool surface)
- [x] Sync `netclaw-onboarding` delta spec (memory step, 9-step wizard)
- [x] Sync `netclaw-cli` delta spec (doctor + status memory checks)

### Task 4.2: Update ADR-002
- [ ] Update ADR-002 to reflect unified memory provider architecture
- [ ] Document file-based backend as default, Memorizer as upgrade path
