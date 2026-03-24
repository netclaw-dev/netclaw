## Context

Netclaw's skill system currently treats all skills equally — no distinction
between system skills (compiled into binary), operator-placed skills, and
future community/external skills. The `SkillScanner` only processes `.system/`
as a nested hidden directory; other dot-prefixed directories are skipped.

A security scanning pipeline is needed for community/external skills but the
real implementation will share infrastructure with the webhook prompt injection
detection system. This change establishes the interface and no-op stub.

The `skill-authoring` system skill was removed during a recent cleanup. It
needs to come back with updated documentation covering the Claude Code-style
invocation fields.

**Current components:**
- `SkillScanner` Pass 2 (line 71): `if (subDirName.StartsWith('.') && !subDirName.Equals(".system"))` — only `.system/` is processed
- `SkillEntry` has no trust tier property
- `Netclaw.Security` project has `IContentScanner`, `IPromptInjectionDetector` — patterns to follow
- `feeds/skills/.system/files/` contains existing system skills

## Goals / Non-Goals

**Goals:**
- Establish trust tier infrastructure for future community/external skill support
- Provide `ISkillContentScanner` interface for `skill_manage` integration
- Restore `skill-authoring` system skill with complete frontmatter documentation
- Update existing system skills with invocation control fields

**Non-Goals:**
- Implementing real content scanning (deferred to shared prompt injection work)
- Community feed sync service (separate change)
- CLI skill management commands
- Quarantine flow implementation

## Decisions

### D1: Trust tier inferred from directory, not frontmatter

**Decision:** The trust tier is determined by which directory the skill lives
in: `.system/` → System, root → Operator, `.community/` → Community,
`.external/` → External, `.agent/` → Agent.

**Why:** A skill cannot self-declare a higher trust tier. If frontmatter
controlled trust, a malicious skill could claim `System` tier. Directory
location is controlled by the installation mechanism, not the skill content.

### D2: No-op scanner stub (not omitted entirely)

**Decision:** Ship `ISkillContentScanner` with a `NoOpSkillContentScanner`
that always returns `IsAllowed = true`. Wire it into `skill_manage` now.

**Why:** The integration point needs to exist before the real implementation.
Without the stub, `skill_manage` would need to be modified later to add
scanning. With the stub, the scanning pipeline is already wired — only the
implementation changes.

### D3: Existing system skills get invocation field updates

**Decision:** Update frontmatter on existing system skills:
- `netclaw-operations` → `disable-model-invocation: true` (user invokes when
  they want ops routing; LLM shouldn't auto-load this for every message)
- `netclaw-diagnostics` → `disable-model-invocation: true` (same reasoning)
- `netclaw-memory` → keep model-invocable (agent should auto-load for memory queries)
- `search-citation` → keep model-invocable (agent should auto-load for web searches)
- `netclaw-manual` → `user-invocable: false` (reference material, not a workflow)

**Why:** Operations and diagnostics are side-effect workflows where timing
matters. The user should explicitly request them. Memory and search-citation
are background guidance that the LLM should load whenever relevant.

## Risks / Trade-offs

**[R1] Expanding hidden-directory scanning could pick up unexpected directories**
→ Mitigation: The allowlist is explicit: `.system`, `.community`, `.external`,
`.agent`. Any other dot-prefixed directory is still skipped. `.quarantine` is
explicitly excluded.

**[R2] No-op scanner provides false sense of security**
→ Mitigation: The no-op is clearly documented as a stub. The GitHub issue
tracks the real implementation. The trust tier system provides the first layer
of defense (visibility restrictions).

## Actor Boundaries and Persistence Implications

- No new actors or persistence events.
- `SkillTrustTier` is a property on `SkillEntry`, which is a transient
  in-memory record. Not persisted.
- `ISkillContentScanner` is registered in DI, injected into `SkillManageTool`.
