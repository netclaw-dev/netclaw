## 1. SubAgent Configuration

- [x] 1.1 Create `SubAgentConfig` class in `Netclaw.Configuration` with `DefaultTimeoutSeconds` (60), `StoreMemoryTimeoutSeconds` (180), `SearchMemoriesTimeoutSeconds` (30)
- [x] 1.2 Bind `SubAgents` config section in `Program.cs` (`configuration.GetSection("SubAgents").Get<SubAgentConfig>() ?? new SubAgentConfig()`) and register as singleton
- [x] 1.3 Inject `SubAgentConfig` into `MemorizerStoreMemoryTool` and `MemorizerSearchMemoriesTool` — replace hardcoded `TimeSpan` values with config-driven timeouts
- [x] 1.4 Update `netclaw-config.v1.schema.json` — add `Memory` object property (`Provider` enum: `["files", "memorizer"]`, default `"files"`) and `SubAgents` object property (`DefaultTimeoutSeconds`, `StoreMemoryTimeoutSeconds`, `SearchMemoriesTimeoutSeconds` — all integer, minimum 5, maximum 600)
- [x] 1.5 Add `SubAgents` section validation to `netclaw doctor` — timeout values must be positive integers between 5 and 600
- [x] 1.6 Tests: verify default config values, verify custom config overrides timeout, verify doctor rejects invalid values, verify schema validates Memory and SubAgents sections

## 2. SubAgent Observability

- [x] 2.1 Define `SubAgentNotification` record (AgentName, Phase, ToolCount, Success, Duration) and `SubAgentPhase` enum (Started, Completed) in `Netclaw.Actors/SubAgents/`
- [x] 2.2 Add `Action<SubAgentNotificationInfo>? OnSubAgentActivity` property to `ToolExecutionContext`
- [x] 2.3 Define `SubAgentOutput : SessionOutput` record in `Protocol/SessionOutput.cs` — filtered under `OutputFilter.ToolCalls`
- [x] 2.4 Add `SubAgentOutput` arms to `SessionOutputDtoMapper.cs` (ToDto and FromDto) with discriminator `"subagent"`
- [x] 2.5 Wire `OnSubAgentActivity` callback in `LlmSessionActor`'s `ExecuteToolsAsync` pipeline — convert notifications to `SubAgentOutput` and publish to subscribers
- [x] 2.6 Update `MemorizerStoreMemoryTool` and `MemorizerSearchMemoriesTool` to invoke `ToolExecutionContext.OnSubAgentActivity` on subagent start/completion
- [x] 2.7 Render `SubAgentOutput` in `HeadlessChannel.cs`: `[subagent:start]` and `[subagent:done]` format
- [x] 2.8 Tests: verify SubAgentOutput DTO round-trip (started + completed), verify schema validation for Memory/SubAgents sections

## 3. Context Layer Update

- [x] 3.1 Update `MemorizerConnected` text in `MemoryIndexContextLayer.cs` — add subagent delegation note and latency expectation (10–30 seconds)
- [x] 3.2 Update `SearchMemoriesToolTests` context layer assertion to include subagent note

## 4. Init Wizard — Memory Step

- [x] 4.1 Add `Memory = 6` to `WizardStep` enum, shift Exposure=7, Identity=8, HealthCheck=9, set `TotalSteps = 9`
- [x] 4.2 Add memory-related ViewModel state: `SelectedMemoryBackend` (string, default "files"), `MemorizerTransport` (string), `MemorizerUrl` (string), `MemorizerCommand` (string), `MemorizerArguments` (string)
- [x] 4.3 Implement memory substep navigation in `GoNext()`/`GoBack()` — substep 0 (select backend), substep 1 (transport, Memorizer only), substep 2 (connection details, Memorizer only)
- [x] 4.4 Update `GetDisplayStepNumber()` for the new step count (no conditional skip — Memory always renders)
- [x] 4.5 Build Memory step UI in `InitWizardPage.cs` — SelectionListNode for backend choice, SelectionListNode for transport, TextInputNode for connection details
- [x] 4.6 Implement Memorizer connectivity probe — HTTP GET for http transport, assume-reachable for stdio transport, 10-second timeout
- [x] 4.7 Add fallback UX — health check reports degraded (not failed) when Memorizer unreachable
- [x] 4.8 Wire `WriteConfig()` — write `Memory.Provider` and `McpServers.memorizer` entry to `netclaw.json` based on selected backend and connection details

## 5. Init Wizard — Health Check Integration

- [x] 5.1 Add Memorizer reachability check to `RunHealthCheckAsync()` — only when `Memory.Provider = "memorizer"`
- [x] 5.2 Report as degraded (warning) not failed when Memorizer unreachable — message: "Memorizer unreachable — memory will use local files"
- [x] 5.3 Tests: verify health check passes when Memorizer reachable, verify degraded when unreachable, verify skipped when provider is files

## 6. Init Wizard Tests

- [x] 6.1 Test memory step navigation: select files → advances immediately, select Memorizer → enters substeps (covered by GoNext_AdvancesStep update)
- [x] 6.2 Test config output: files selection writes correct Memory.Provider, Memorizer selection writes both Memory.Provider and McpServers entry
- [x] 6.3 Test step count: `TotalSteps == 9`, `GetDisplayStepNumber` correct for all steps
- [x] 6.4 Test back navigation through memory substeps (covered by substep back handling in HandleSubStepBack)
- [x] 6.5 Test fallback from Memorizer probe failure to local files (HealthCheck_MemorizerUnreachable_ReportsDegradedNotFailed)

## 7. Verification

- [x] 7.1 `dotnet build` — zero errors
- [x] 7.2 `dotnet test` — all existing + new tests pass (544 total)
- [x] 7.3 `dotnet slopwatch analyze` — zero new violations
- [ ] 7.4 Smoke test: run `netclaw init` through the Memory step with local Memorizer, verify config written correctly
- [ ] 7.5 Smoke test: run headless prompt with Memorizer-backed memory, verify `[subagent:start]` and `[subagent:done]` appear in output
