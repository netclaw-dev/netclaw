## Why

The subagent mechanism (SubAgentActor + Memorizer-backed `store_memory` /
`search_memories`) works end-to-end but is invisible to users. There is no
wizard step to choose a memory backend, no way to configure Memorizer through
`netclaw init`, no observability when a subagent runs, and timeouts are
hardcoded. Without these, subagents are a developer-only feature that
requires manual JSON editing to enable.

## What Changes

- **Init wizard memory step**: Add a Memory step to `netclaw init` that lets
  operators choose file-backed (default) or Memorizer-backed memory. When
  Memorizer is selected, collect MCP server connection details (transport, URL
  or command) and validate connectivity before proceeding. Write
  `Memory.Provider` and `McpServers.memorizer` to `netclaw.json`.
- **Init wizard health check integration**: The existing HealthCheck step
  validates Memorizer reachability when `Memory.Provider = "memorizer"`. Report
  degraded (not failed) if Memorizer is unreachable — file-backed is the
  implicit fallback.
- **Subagent observability events**: Emit structured `SubAgentStarted` and
  `SubAgentCompleted` events through the session output system so subscribers
  (Slack, TUI, headless logs) can see when a subagent spawns, what tools it
  has, and whether it succeeded. Wire through existing `OutputFilter` so
  subscribers can opt in/out.
- **Configurable subagent timeouts**: Move the hardcoded 3-minute (store) and
  30-second (search) timeouts to `netclaw.json` under a `SubAgents` config
  section with sensible defaults. Slower models on CPU need longer timeouts.
- **Context layer Memorizer awareness**: Update the `MemorizerConnected`
  context layer text to explain that `store_memory` and `search_memories` use
  curation subagents (so the frontline model understands why tool calls may
  take longer and can set user expectations).

## Capabilities

### New Capabilities

- `netclaw-subagents`: Subagent lifecycle, configuration (timeouts, model
  role), observability events, and the SubAgentActor execution contract.

### Modified Capabilities

- `netclaw-onboarding`: Add Memory step to the init wizard (provider selection,
  Memorizer MCP configuration, connectivity validation, health check
  integration).
- `netclaw-agent-memory`: Add Memorizer-backed memory tools as a provider
  option alongside file-backed. Document unified `store_memory` /
  `search_memories` interface and context layer behavior per backend.

## Impact

- **Init wizard**: New `WizardStep.Memory` between current steps (likely after
  Provider, since memory backend choice depends on knowing the LLM provider is
  working). Adds ~1 new step to the 8-step flow. Touches
  `InitWizardPage.cs`, `InitWizardViewModel.cs`, and their tests.
- **Session output protocol**: New `SubAgentOutput` event type in
  `SessionOutput`. Touches `Protocol/`, subscriber filtering, and any adapter
  that renders output (Slack, TUI, headless).
- **Configuration schema**: New `SubAgents` section in `netclaw.json` for
  timeout overrides. Touches `NetclawConfig`, `ConfigurationExtensions`,
  validation in `netclaw doctor`.
- **Context layer**: Minor text change in `MemoryIndexContextLayer.cs`
  (already touched in the subagent-core PR).
- **No breaking changes**. All new behavior is additive. Existing
  configurations continue to work — file-backed remains the default, timeouts
  fall back to current hardcoded values if config section is absent.
