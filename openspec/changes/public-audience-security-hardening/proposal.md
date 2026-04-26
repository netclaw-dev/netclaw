## Why

Discord integration testing with a public audience disposition revealed that
public sessions leak internal operating instructions (AGENTS.md), filesystem
paths (session directory, project directory, allowed file roots), and can
inject tainted memories that are later recalled into privileged sessions
(#755). The current architecture treats AGENTS.md as operator-mutable and
loads the same identity files for all audiences. There is no mechanism for
operators to selectively enable or disable capabilities per deployment
posture, and context layers inject the same content regardless of trust level.

## What Changes

- **AGENTS.md becomes binary-controlled firmware.** The runtime loads
  audience-specific AGENTS variants from embedded resources instead of
  the filesystem. Operators can no longer edit AGENTS.md — it is alignment
  firmware analogous to Claude Code's embedded instructions. SOUL.md and
  TOOLING.md remain operator-mutable. **BREAKING**: existing AGENTS.md
  files on disk are no longer read at runtime.
- **New wizard step for feature selection.** When operators select a
  non-Personal deployment posture during `netclaw init`, a new Feature
  Selection step presents toggleable capabilities (memory, skills,
  scheduling, subagents, webhooks, web search) with audience-appropriate
  defaults. Selections write `Enabled` flags to config and drive which
  sections appear in the runtime AGENTS.md.
- **Config schema gains `Enabled` flags** for Memory, Skills, Scheduling,
  SubAgents, and Webhooks sections. These are the runtime source of truth
  for feature availability.
- **Context assembly filters by audience.** `IContextLayerProvider` and
  `ContextAssemblyInput` gain a `TrustAudience` parameter. Public sessions
  receive: no skill index, no memory index, no subagent discovery, no
  working context, and a redacted session block (ID only, no filesystem
  paths).
- **Memory fully disabled for public sessions.** Memory tools removed from
  public audience profile, automatic recall suppressed, memory
  extraction/distillation skipped. Eliminates the memory taint vector.
- **File access error messages sanitized.** Public-audience denial messages
  no longer enumerate allowed root paths.

## Capabilities

### New Capabilities

- `audience-context-filtering`: Runtime filtering of context layers,
  session blocks, working context, and error messages by TrustAudience.
  Covers the `IContextLayerProvider` audience parameter, session block
  redaction, working context suppression, and error message sanitization.
- `feature-selection-wizard`: New wizard step for non-Personal postures
  presenting toggleable feature capabilities. Includes config `Enabled`
  flags for Memory, Skills, Scheduling, SubAgents, and Webhooks.

### Modified Capabilities

- `netclaw-session`: System prompt assembly gains audience parameter;
  AGENTS.md loaded from embedded resources by audience instead of from
  disk. `ContextAssemblyInput` gains `TrustAudience Audience` field.
- `netclaw-tools`: Public audience profile loses memory tools
  (`store_memory`, `find_memories`, `get_memories`, `update_memory`).
  File access denial messages sanitized for public audience.
- `netclaw-agent-memory`: Memory recall, extraction, and distillation
  gated on `MemoryConfig.Enabled` and audience. Public sessions are
  fully amnesic.
- `netclaw-onboarding`: Init wizard stops writing AGENTS.md to disk.
  New Feature Selection step inserted after Security Posture.
- `security-posture-tui`: Feature Selection step reads
  `SelectedPosture` from `WizardContext` to determine defaults.

## Impact

- **Config schema**: New `Enabled` properties on Memory, SkillSync,
  SubAgents, and Webhooks sections. New `Scheduling` section. Existing
  deployments unaffected (defaults match current behavior for
  Personal posture).
- **System prompt provider**: `ISystemPromptProvider.GetSystemPrompt()`
  signature changes (adds `TrustAudience` parameter). All callers
  must update.
- **Context layer interface**: `IContextLayerProvider.GetContextLayer()`
  signature changes (adds `TrustAudience` parameter). All
  implementations must update.
- **Init wizard**: AGENTS.md no longer written to disk. Existing
  AGENTS.md files ignored at runtime. Operators who customized
  AGENTS.md need to migrate customizations to SOUL.md or contact
  support.
- **Breaking for AGENTS.md customizers**: Any operator who edited
  `~/.netclaw/identity/AGENTS.md` after init will lose those
  customizations. This is intentional — AGENTS.md is alignment
  firmware and should not be operator-mutable.
