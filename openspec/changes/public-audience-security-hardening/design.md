## Context

Netclaw's public audience posture was designed to restrict tool access (no
shell, limited filesystem, no MCP servers) but left several context
injection paths unfiltered. During Discord integration testing, adversarial
probing revealed that public sessions receive the same AGENTS.md identity
file, the same context layers (skill index, memory index, subagent
discovery), full filesystem paths in the session block, and can write
memories that later surface in privileged sessions. The current
`ISystemPromptProvider` and `IContextLayerProvider` interfaces have no
audience parameter, and the init wizard applies smart defaults silently
without operator input.

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
```

### Constraints

- AGENTS.md is alignment firmware — its content must match the binary
  version, not be operator-editable.
- SOUL.md and TOOLING.md remain operator-mutable.
- The wizard uses Termina TUI with `SelectionListNode` and custom checkbox
  rendering (see `ExternalSkillsStepView` for the pattern).
- `IContextLayerProvider` is a simple interface with a single
  `GetContextLayer()` method. Adding a parameter is a breaking interface
  change but all implementations are internal.
- Config schema uses `additionalProperties: false` — new properties must
  be added to the schema in the same PR.

## Goals / Non-Goals

**Goals:**

- Eliminate information leakage from public sessions: no internal operating
  instructions, no filesystem paths, no memory taint vector
- Give operators explicit control over feature availability per deployment
  posture via the init wizard
- Make AGENTS.md binary-owned so the runtime always uses the correct
  audience-specific variant
- Config `Enabled` flags become the runtime source of truth for feature
  availability, overriding audience profile defaults when disabled

**Non-Goals:**

- Per-channel feature toggles (feature selection is per-deployment, not
  per-channel)
- Runtime AGENTS.md hot-reload (loaded once at session start, cached)
- Custom operator AGENTS.md content (this is explicitly being removed)
- Memory purge of existing public-audience data (separate concern)
- Dynamic AGENTS.md section assembly based on config flags at runtime
  (deferred — for this change, audience variants are static embedded
  resources)

## Decisions

### D1: AGENTS.md loaded from embedded resources, not filesystem

**Choice:** Embed audience-specific AGENTS.md files as assembly resources
in `Netclaw.Configuration`. `FileSystemPromptProvider` loads from the
embedded resource based on the session's `TrustAudience`.

**Rationale:** This prevents operators from editing alignment rules and
prevents public sessions from seeing personal-audience operating
instructions. The binary is the single source of truth for agent behavior.

**Alternatives considered:**
- *Filesystem with audience-specific filenames* (AGENTS.public.md): Still
  operator-editable, doesn't solve the ownership problem.
- *Runtime section stripping*: Fragile heuristic, hard to maintain, easy
  to break by editing the template.

### D2: Feature Selection wizard step with config `Enabled` flags

**Choice:** New `FeatureSelectionStepViewModel` presented after Security
Posture for non-Personal postures. Toggleable features write `Enabled`
boolean flags to the config. Runtime code checks these flags to gate
feature availability.

**Rationale:** Operators deploying for public or team use should
explicitly opt into capabilities rather than discovering restrictions
after deployment. The checkbox pattern already exists in
`ExternalSkillsStepView`.

**Alternatives considered:**
- *Automatic defaults only*: Current approach — operators don't know what's
  enabled until they test it.
- *Post-init config editing*: Requires operators to understand the config
  schema. The wizard is the primary onboarding surface.

### D3: Context layer audience parameter

**Choice:** Add `TrustAudience audience` parameter to
`IContextLayerProvider.GetContextLayer()` and `ContextAssemblyInput`.
Each implementation decides what to return per audience.

**Rationale:** Simplest extension point. All implementations are internal,
so the breaking interface change has no external impact.

**Alternatives considered:**
- *Separate filtering layer*: More complex, adds indirection without
  benefit since each layer knows best what to suppress.
- *Audience-aware layer registration*: Over-engineered for 3
  implementations.

### D4: Session block path redaction via audience check

**Choice:** `SessionMessageAssembler.BuildStaticContextBlock()` checks the
audience from `ContextAssemblyInput`. For Public, emits only the session
ID. For Team/Personal, includes full paths.

**Rationale:** Session ID is the channel/thread ID visible in the
Discord/Slack UI — not an information leak. Filesystem paths reveal
deployment topology.

### D5: Memory full disable via audience + config flag

**Choice:** Two-layer gate: (1) Public audience profile loses memory
tools, (2) `MemoryConfig.Enabled` flag gates recall and extraction at
runtime. Both must be true for memory to function.

**Rationale:** The audience profile controls what the LLM can invoke. The
config flag controls whether the infrastructure runs. Belt and suspenders.

### D6: Error message sanitization for public audience only

**Choice:** `ScopedFileAccessPolicy` omits allowed root paths from error
messages for Public audience. Team/Personal retain verbose errors.

**Rationale:** Verbose errors help operators debug. Public users don't
need to know the deployment's directory structure.

## Risks / Trade-offs

- **[Risk] Existing AGENTS.md customizations lost** → Migration: document
  the change. Operators who customized AGENTS.md should move behavioral
  guidance to SOUL.md. The set of operators who have done this is small
  (product is pre-1.0).

- **[Risk] Feature flags add config complexity** → Mitigation: wizard
  handles initial setup; defaults match current behavior for Personal
  posture. Config is only complex if operators edit it manually.

- **[Risk] IContextLayerProvider interface change breaks external
  implementations** → Mitigation: all implementations are internal. No
  public API contract.

- **[Trade-off] Static embedded AGENTS.md vs. runtime section assembly**
  → We ship two static files (full and public) rather than dynamically
  assembling sections based on config flags. This means config flag
  changes (e.g., enabling memory on a public deployment) won't update
  AGENTS.md guidance until we implement dynamic assembly in a future
  change. Acceptable for MVP because the audience-specific variants cover
  the critical security case.

- **[Trade-off] TOOLING.md suppressed entirely for Public** → Public
  sessions lose environment context. Acceptable because public sessions
  have minimal tool access anyway, and TOOLING.md exposes deployment
  details.
