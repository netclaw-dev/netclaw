## Context

Netclaw's public audience posture was designed to restrict tool access (no
shell, limited filesystem, no MCP servers) but left several context
injection paths unfiltered. During Discord integration testing, adversarial
probing revealed that Public sessions receive the same AGENTS.md identity
file, the same context layers (skill index, memory index, subagent
discovery), full filesystem paths in the session block, and can write
memories that later surface in privileged sessions. The current
`ISystemPromptProvider` and `IContextLayerProvider` interfaces have no
audience parameter, and the init wizard applies smart defaults silently
without operator input.

The remaining transport-specific gap is timing: Slack/Discord session startup
can still assemble the initial prompt and startup context before the resolved
channel audience is threaded through, which means a Public-origin session can
briefly receive the wrong AGENTS variant or capability index on its first
turn.

The remaining gap is not just prompt injection. Several capability-discovery
and load paths still provide side channels: MCP `search_tools` / `load_tool`,
skill tools, subagent discovery/spawn, and the global implicit file roots that
currently expose identity, skills, and workspaces content to all audiences.
Public hardening is only implementation-ready if these paths use the same
audience/feature decisions as direct prompt injection and direct invocation.

### Current Architecture

```
netclaw init
  └─ IdentityStepViewModel.WriteIdentityFiles()
       └─ Writes AGENTS.md from embedded template to ~/.netclaw/identity/AGENTS.md

Session start
  └─ FileSystemPromptProvider.GetSystemPrompt(projectDir)
       └─ Reads SOUL.md, AGENTS.md, TOOLING.md from disk
       └─ Passes to SystemPromptAssembler.Assemble(soul, agents, tooling, project)

Each turn
  └─ SessionMessageAssembler.Assemble(ContextAssemblyInput)
       └─ BuildStaticContextBlock: context layers + session block (paths)
       └─ BuildVolatileContextBlock: recall + working context
       └─ No audience parameter anywhere in this chain

Discovery/load side channels
  ├─ MCP: search_tools -> load_tool
  ├─ skills: skill_load -> skill_read_resource
  ├─ subagents: discovery layer -> spawn_agent
  └─ files: GlobalReadRoots -> identity / skills / workspaces
```

### Constraints

- AGENTS.md is alignment firmware. Its content must match the binary
  version, not be operator-editable.
- SOUL.md and TOOLING.md remain operator-mutable.
- The wizard uses Termina TUI with `SelectionListNode` and custom checkbox
  rendering.
- `IContextLayerProvider` is a simple interface with a single
  `GetContextLayer()` method. Adding a parameter is a breaking interface
  change but all implementations are internal.
- Config schema uses `additionalProperties: false`.
- Existing feature config types are uneven. Some subsystems already have a
  natural config section (`Memory`, `Search`, `SkillSync`, `SubAgents`,
  `Webhooks`), while scheduling currently relies more on actor/service wiring
  than an explicit top-level on/off switch.
- Public currently relies on `ToolAudienceProfiles.CreatePublic()` plus
  `GlobalReadRoots`, which means some internal roots are implicitly reachable
  even though Public read/write tool modes are session-scoped.

## Goals / Non-Goals

**Goals:**

- Eliminate information leakage from Public sessions: no internal operating
  instructions, no filesystem paths, no hidden capability discovery, no memory
  taint vector.
- Give operators explicit control over deployment-wide feature runtime wiring
  while keeping audience exposure as a separate allowlist decision.
- Make AGENTS.md binary-owned so the runtime always uses the correct
  audience-specific variant.
- Make prompt injection, discovery results, direct invocation, implicit file
  roots, and automatic/background execution all agree on the same audience /
  feature decisions.
- Clarify that Public sessions cannot write memories or perform recall/search,
  while historical Public-authored memories remain available to trusted
  higher-privilege contexts under their normal policy and may still be deleted
  deliberately from those contexts.

**Non-Goals:**

- Per-channel feature toggles.
- Runtime AGENTS.md hot-reload.
- Custom operator AGENTS.md content.
- Memory purge of existing Public-audience data.
- New data migration or cleanup code for existing Public memories.
- Dynamic AGENTS.md section assembly based on config flags at runtime.
- Large ACL redesign or a new policy language.

## Decisions

### D1: AGENTS.md loaded from embedded resources, not filesystem

**Choice:** Embed audience-specific AGENTS.md files as assembly resources.
`FileSystemPromptProvider` loads from the embedded resource based on the
session's `TrustAudience`.

**Rationale:** This prevents operators from editing alignment rules and
prevents Public sessions from seeing Personal/Team operating instructions.

### D2: Deployment-wide `Enabled` switches are distinct from audience allowlists

**Choice:** Add deployment-wide `Enabled` switches to the relevant subsystem
config sections (`Memory`, `Search`, `SkillSync`, `SubAgents`, `Webhooks`) and
add a new top-level `Scheduling` config section whose only property in this
change is `Enabled`. These switches decide whether runtime services,
registries, watchers, and tool registration are active at all. Audience
profiles continue to decide which audiences may discover or invoke a
still-enabled subsystem.

**Rationale:** The repo already uses audience profiles for exposure and tool
allowlisting. The missing piece is an operator-controlled runtime kill switch.

**Consequences:**

- `Search.Enabled = false` means no `web_search`/`web_fetch` registration for
  any audience.
- `Search.Enabled = true` with Public `AllowedTools` omitting search still means
  Public cannot see or use search.
- `Scheduling.Enabled = false` means reminder tools and scheduled reminder
  execution are off for all audiences.
- Background jobs remain governed by shell/background-job policy rather than
  the new `Scheduling` config section.
- The same pattern applies to memory, skills, subagents, and webhooks.

### D3: Feature Selection configures deployment-wide switches, not implicit Public allowlists

**Choice:** New `FeatureSelectionStepViewModel` presented after Security
Posture for non-Personal postures. Toggleable features write deployment-wide
`Enabled` flags to config. Public posture defaults search off. Enabling search
there does not mutate `Tools.AudienceProfiles.Public.AllowedTools`; Public
search exposure still requires explicit operator allowlisting.

**Rationale:** The wizard should make runtime posture clear without silently
rewriting audience policy.

### D4: Context layer audience parameter

**Choice:** Add `TrustAudience audience` parameter to
`IContextLayerProvider.GetContextLayer()` and `ContextAssemblyInput`.

**Rationale:** Smallest useful extension point.

### D5: Discovery/load tools use the same audience and feature gates as direct exposure

**Choice:** `search_tools`, `load_tool`, `skill_load`, `skill_read_resource`,
subagent discovery, and `spawn_agent` resolve visibility from the same
effective audience + feature flags used by direct tool exposure.

**Rationale:** Hiding capabilities only in the prompt or initial tool list is
insufficient if meta-tools can still enumerate or reactivate them.

### D6: Session block path redaction via audience check

**Choice:** `SessionMessageAssembler.BuildStaticContextBlock()` emits only the
session ID for Public. Team/Personal retain full paths.

**Rationale:** Session ID is already visible in the UI. Filesystem paths reveal
deployment topology.

**Startup timing rule:** The effective audience must be resolved before the
first call to `GetSystemPrompt()` and before startup tool/context indices are
assembled for channel-created sessions. Slack and Discord session startup must
therefore thread the resolved audience into the very first prompt/context
construction path rather than correcting it only after the session is already
running.

### D7: Public loses implicit internal file roots

**Choice:** Public file access remains session-root scoped only. Identity,
skills, and workspaces roots stop being global implicit read roots for Public.

**Rationale:** Public should not pivot from file tools into internal
identity/skill/workspace content through convenience defaults.

### D8: Memory full disable via audience + config flag

**Choice:** Two-layer gate: Public audience profile loses memory tools, and
`MemoryConfig.Enabled` gates recall, explicit search/get, extraction, and
storage-related paths at runtime.

**Rationale:** Audience profile controls invocation. Config controls whether the
infrastructure runs.

**Historical data rule:** Existing Public-authored memories are not purged by
this change. Public sessions lose memory writes and recall/search entirely, but
historical Public-authored items are not globally suppressed from Team/Personal
contexts by this change. Deliberate review or deletion by a higher-privilege
operator remains an operator action, not a new runtime feature in this change.

### D9: Automatic/background execution keeps persisted originating audience and feature scope

**Choice:** Scheduling, webhook execution, and reminder delivery continue to use
the persisted originating audience/boundary and must also respect
deployment-wide `Enabled` switches. Background jobs remain governed by their
existing shell/background-job controls and are not toggled by
`Scheduling.Enabled` in this change.

**Rationale:** Autonomous/runtime-owned paths are where policy drift often
reappears.

### D10: Error message sanitization for Public audience only

**Choice:** `ScopedFileAccessPolicy` omits allowed root paths from error
messages for Public, including any mention of the session directory as an
allowed root. Team/Personal retain verbose errors.

### D11: Public AGENTS attachment guidance must match redacted path policy

**Choice:** The embedded Public AGENTS variant describes attachments using
pathless, session-redacted wording that matches the Public session block and
attachment metadata. It must not instruct the model to inspect `session_dir`,
`media_dir`, `inbox/`, or any other filesystem path that is intentionally
hidden from Public.

**Rationale:** Public prompt guidance and runtime redaction must agree. If the
prompt mentions attachment filesystem locations that the session block hides,
the prompt itself becomes an information leak and trains the model to ask for
nonexistent/path-redacted details.

## Risks / Trade-offs

- **Existing AGENTS.md customizations lost**: operators who customized AGENTS.md
  should move behavioral guidance to SOUL.md.
- **Feature flags add config complexity**: mitigated by the wizard and by
  keeping runtime switches separate from audience allowlists.
- **Distinguishing runtime switches from audience allowlists is easy to
  implement inconsistently**: mitigated by explicit spec/tasks and tests for
  both runtime-disabled and audience-not-exposed cases.
- **IContextLayerProvider interface change breaks implementations**: acceptable
  because all implementations are internal.
- **Static audience-specific AGENTS variants vs. fully dynamic prompt
  assembly**: keep this change minimal by fixing the critical audience split
  first.
- **TOOLING.md suppressed entirely for Public**: Public loses environment
  context, but that content exposes deployment details.
- **No purge/migration for legacy Public memory rows**: keeps the hardening
  change implementation-ready while preserving trusted-context access to
  historical Public-authored data.
