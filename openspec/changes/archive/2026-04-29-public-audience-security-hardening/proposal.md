## Why

Discord integration testing with a public audience disposition revealed that
public sessions leak internal operating instructions (AGENTS.md), filesystem
paths (session directory, project directory, allowed file roots), and can
inject tainted memories that are later recalled into privileged sessions
(#755). The current architecture treats AGENTS.md as operator-mutable and
loads the same identity files for all audiences. There is no mechanism for
operators to selectively disable feature subsystems deployment-wide while also
controlling which audiences may discover or use them. Several discovery and
load paths still let Public recover hidden internals even when direct prompt
injection is filtered: `search_tools`, `load_tool`, `skill_load`,
`skill_read_resource`, `spawn_agent`, and implicit filesystem roots.

Source PRDs: `PRD-002` (SEC-003, SEC-008), `PRD-004`, `PRD-007`, `PRD-008`,
`PRD-009`. Source issue: `#755`.

## What Changes

- **AGENTS.md becomes binary-controlled firmware.** The runtime loads
  audience-specific AGENTS variants from embedded resources instead of
  the filesystem. Operators can no longer edit AGENTS.md. SOUL.md and
  TOOLING.md remain operator-mutable. **BREAKING**: existing AGENTS.md
  files on disk are no longer read at runtime.
- **Deployment-wide runtime kill switches become explicit.** Config gains
  `Enabled` flags for Memory, Search, SkillSync, SubAgents, Scheduling,
  and Webhooks. `Scheduling` is a new top-level config section whose only
  property in this change is `Enabled`. It governs reminder/scheduled execution
  runtime, not background-job shell infrastructure. These switches control
  whether the subsystem is wired up at all. Audience allowlists remain a
  separate control plane for what a session may discover or invoke.
- **New wizard step for feature selection.** When operators select a
  non-Personal deployment posture during `netclaw init`, a new Feature
  Selection step presents deployment-wide feature toggles with posture-specific
  defaults. For Public posture, search defaults off. Enabling search there only
  enables the deployment-wide runtime; it does not automatically expose
  `web_search` or `web_fetch` to Public sessions.
- **Context assembly filters by audience from session start.**
  `IContextLayerProvider` and `ContextAssemblyInput` gain a
  `TrustAudience` parameter. Public sessions receive: no skill index, no
  memory index, no subagent discovery, no working context, and a redacted
  session block (ID only, no filesystem paths). Slack- and Discord-created
  sessions must resolve the effective audience before the initial system
  prompt and startup context are assembled so the first turn uses the right
  audience-specific AGENTS variant and capability index.
- **Discovery and load paths honor the same audience/feature rules.** Public
  must not recover hidden internals through `search_tools`, `load_tool`,
  `skill_load`, `skill_read_resource`, `spawn_agent`, or equivalent capability
  discovery paths. Blocked tools/skills/subagents must be absent from both
  prompt guidance, startup tool/context indices, and discovery results, not
  merely denied at final invocation.
- **Memory fully disabled for Public sessions.** Memory tools removed from the
  Public audience profile, automatic recall suppressed, explicit recall/search
  denied, and memory extraction/distillation skipped. Legacy Public-authored
  memories do not need to be globally suppressed in trusted contexts by this
  change, and higher-privilege sessions may still review or delete them through
  their existing privileged paths. This change does not add purge or cleanup
  behavior.
- **Public file access loses implicit internal roots.** Public file access must
  stay session-scoped by default and must not implicitly reach identity,
  skills, or workspaces content through global roots or similar defaults.
  Public denial messages must not reveal any allowed root, including the
  session directory.
- **Public AGENTS attachment wording stays pathless.** The Public AGENTS
  variant must describe uploaded attachments in the same redacted/pathless
  terms used by the Public session block instead of referring to `session_dir`,
  `media_dir`, `inbox/`, or other filesystem-oriented guidance.
- **Automatic/runtime-owned behavior uses the same gates.** Scheduling and
  webhook entry points must honor both deployment-wide `Enabled` switches and
  the persisted originating audience without widening capability exposure.
- **Identity/system-prompt validation is mandatory.** Because this change
  modifies AGENTS ownership and prompt assembly, the implementation must run
  the behavioral eval suite in addition to build/test/slopwatch.

## Capabilities

### New Capabilities

- `audience-context-filtering`: Runtime filtering of context layers,
  session blocks, working context, and error messages by TrustAudience.
  Covers the `IContextLayerProvider` audience parameter, session block
  redaction, working context suppression, and error message sanitization.
- `feature-selection-wizard`: New wizard step for non-Personal postures
  presenting deployment-wide feature toggles. Includes config `Enabled`
  flags for Memory, Search, SkillSync, Scheduling, SubAgents, and Webhooks.

### Modified Capabilities

- `netclaw-session`: System prompt assembly gains audience parameter;
  AGENTS.md loaded from embedded resources by audience instead of from
  disk. `ContextAssemblyInput` gains `TrustAudience Audience` field.
- `netclaw-input-adapters`: Channel-created sessions must propagate the
  resolved audience before first-turn prompt/context assembly so Slack and
  Discord sessions start with the correct audience-specific prompt and
  capability surface.
- `netclaw-tools`: Public audience profile loses memory tools and defaults to
  `web_search` / `web_fetch` disabled unless explicitly allowlisted. File
  access denial messages are sanitized for Public audience, and Public loses
  implicit internal file roots.
- `netclaw-agent-memory`: Memory recall, extraction, and distillation
  gated on `MemoryConfig.Enabled` and audience. Public sessions are
  fully amnesic, and historical Public memories become inert for normal
  recall/search going forward.
- `netclaw-mcp`: `search_tools` and `load_tool` must enforce the same
  audience/feature filters as direct tool exposure and must not reveal blocked
  tools or servers to Public.
- `skill-tools`: `skill_load` and `skill_read_resource` become unavailable when
  the skills subsystem is disabled and for Public sessions.
- `netclaw-subagents`: Public loses subagent discovery and `spawn_agent`
  access. Subagent visibility must follow the same allowlist and runtime gates.
- `netclaw-scheduling`: Scheduling/runtime-owned reminders are gated by a
  deployment-wide `Scheduling.Enabled` switch plus audience/tool allowlists.
- `netclaw-onboarding`: Init wizard stops writing AGENTS.md to disk.
  New Feature Selection step inserted after Security Posture.
- `security-posture-tui`: Feature Selection step reads
  `SelectedPosture` from `WizardContext` to determine defaults.

## Impact

- **Config schema**: New `Enabled` properties on Memory, Search, SkillSync,
  SubAgents, Webhooks, and a new top-level `Scheduling` section. `Scheduling`
  contains only `Enabled` in this change. Existing deployments remain
  enabled-by-default unless operators choose otherwise.
- **System prompt provider**: `ISystemPromptProvider.GetSystemPrompt()`
  signature changes (adds `TrustAudience` parameter). All callers
  must update.
- **Context layer interface**: `IContextLayerProvider.GetContextLayer()`
  signature changes (adds `TrustAudience` parameter). All
  implementations must update.
- **Runtime wiring**: Tool registration, skill sync/watchers, subagent
  registration, reminder/scheduling services, and webhook startup paths must
  all observe deployment-wide `Enabled` switches instead of assuming that
  audience filtering alone is sufficient.
- **Init wizard**: AGENTS.md no longer written to disk. Existing
  AGENTS.md files ignored at runtime. Operators who customized
  AGENTS.md need to migrate customizations to SOUL.md.
- **Breaking for AGENTS.md customizers**: Any operator who edited
  `~/.netclaw/identity/AGENTS.md` after init will lose those
  customizations. This is intentional.
- **No data purge in scope**: Existing Public-authored memories are not deleted
  by this change. Public sessions stop participating in memory write and
  recall/search paths, but trusted higher-privilege contexts do not
  automatically lose access to historical Public-authored memories.
