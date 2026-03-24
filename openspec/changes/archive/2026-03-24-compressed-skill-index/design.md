## Context

Netclaw's `SkillRegistry.GenerateDescriptionMenu()` produces a verbose index
(~2000+ tokens for 7 skills) that includes full descriptions, absolute file
paths, and 6 lines of mandatory-loading instructions per skill. Research from
dotnet-skills evals demonstrates that compressed indexes dramatically
outperform verbose ones — information overload suppresses LLM skill activation.

The current index is also identical for all sessions. With the trust context
system (PR #387) now in place, sessions have different effective audiences
(Public/Team/Personal) with different tool access. Showing skills that require
unavailable tools wastes tokens and confuses the LLM.

**Current components:**
- `SkillRegistry` — mutable list of `SkillEntry`, `GenerateDescriptionMenu()`
  produces verbose text
- `SkillIndexContextLayer` — volatile string, `OnceAtStart` timing, injects
  menu into system prompt
- `ToolAccessPolicy` — knows which tools are visible per audience
- `SkillEntry.AllowedTools` — already parsed from frontmatter
- `SkillEntry.Category` — already inferred from directory structure
- `SessionSidecarRunner` pattern — async LLM sidecar call used elsewhere

## Goals / Non-Goals

**Goals:**
- Reduce skill index token count by 4-5x while improving activation rate
- Generate user-language trigger phrases via LLM sidecar, cached to disk
- Filter skills from index based on session audience and available tools
- Reference `skill_load` tool instead of `file_read` in the index

**Non-Goals:**
- Deterministic pre-LLM keyword matching (deliberately scrapped)
- Replacing the LLM as the skill-loading decision-maker
- Community/external feed support (separate change)
- Modifying skill file content or frontmatter format

## Decisions

### D1: Pipe-delimited compressed format (not markdown list)

**Decision:** Emit a pipe-delimited format grouped by category, similar to
dotnet-skills' evaluated format.

**Target output:**
```
[skills]|load via skill_load(name)|invoke via /name
|ops:{netclaw-operations} — routing for diagnostics, scheduling, identity
|memory:{netclaw-memory} — memory tools, recall guidance, store/search
|search:{search-citation} — web search, citation policy, source handling
```

**Why:** The dotnet-skills evals proved this format achieves 56.5% TPR (vs
21.7% for verbose). Pipe-delimited is more token-dense than markdown lists.
Category grouping gives routing context. Short trigger phrases (not full
descriptions) prevent information overload.

**Why not just truncate descriptions:** Truncation loses meaning. A 60-char
truncation of an operator-written description still uses operator language.
The LLM sidecar generates trigger phrases in user language.

### D2: LLM sidecar for trigger phrases (not hardcoded, not runtime)

**Decision:** Use an `IHostedService` that runs after skill scanning to
generate a short (5-15 word) trigger phrase per skill via LLM sidecar
(`ModelRole.Compaction`). Results cached to disk at
`~/.netclaw/cache/skill-index/{name}-{version}.json`.

**Why LLM:** Skills are written in operator/process language ("cite sources",
"ensure factual claims include URLs") while users speak domain language
("find a restaurant", "buy tickets"). The LLM bridges this vocabulary gap
by understanding what the skill is actually about.

**Why not runtime:** The sidecar runs once per skill version at scan time.
No runtime cost per turn. Cache invalidation by name+version is simple.

**Fallback:** If sidecar is unavailable (no model configured, timeout, first
boot offline), use first 60 chars of `Description` field. Recover on next
startup.

**Cost:** One sidecar call per skill per version. With ~7 system skills and
`ModelRole.Compaction` (cheapest model), this is negligible.

### D3: Audience-aware filtering via available tool set

**Decision:** `GenerateDescriptionMenu()` accepts a `TrustAudience` and an
`IReadOnlySet<string>` of available tool names. Skills whose `AllowedTools`
contain tools not in the available set are excluded.

**Why not filter by trust tier alone:** Trust tier determines baseline
visibility (System/Operator → all audiences, Community → Team+Personal, etc.)
but tool availability is a separate axis. A System skill that requires
`shell_execute` should still be hidden in Public audience where shell is off.
Both filters apply.

**Integration:** `ToolAccessPolicy.FilterExposedTools()` already computes the
visible tool set per audience. Pass that set through to the menu generator.

### D4: Per-audience menu generation at scan time (not per-turn)

**Decision:** Generate menus for each `TrustAudience` value (Public, Team,
Personal) at scan time and cache them. `SkillIndexContextLayer` selects the
appropriate pre-built menu at session start.

**Why not per-turn:** The skill set changes only on rescan (daemon startup or
feed sync). Generating per-turn wastes CPU. Pre-building three menus (one per
audience) at scan time is cheap and deterministic.

**Update trigger:** Menus are rebuilt whenever `SkillRegistry` is cleared and
re-populated (after feed sync or `skill_manage` mutation).

### D5: Trust tier visibility rules

**Decision:** Skills are visible based on their trust tier:
- System (0) / Operator (1) → visible to all audiences
- Community (2) → visible to Team + Personal
- External (3) / Agent (4) → visible to Personal only

A skill cannot widen its visibility beyond its tier default. The
`DisableModelInvocation` flag can further restrict visibility (skill hidden
from LLM index but still user-invokable via slash command).

## Risks / Trade-offs

**[R1] Trigger phrases may not accurately represent skill purpose**
→ Mitigation: Cache files are human-readable JSON. Bad phrases can be
manually deleted to force regeneration. The prompt to the sidecar should be
specific about output format.

**[R2] Compressed format may be too terse for LLMs to route correctly**
→ Mitigation: The dotnet-skills evals empirically validated this format across
multiple models. The trigger phrases add user-language context that pure
category+name lacks.

**[R3] Pre-built menus become stale if tool grants change mid-session**
→ Mitigation: Tool grants are static for the session lifetime (determined at
session start from trust context). Menu staleness only matters across daemon
restarts, which trigger a rescan anyway.

**[R4] Sidecar adds startup latency**
→ Mitigation: Non-blocking. Runs after skill scanning completes. If slow, the
fallback (truncated description) is used until enrichment finishes. No
blocking of session starts.

## Actor Boundaries and Persistence Implications

- `SkillIndexEnrichmentService` is an `IHostedService`, not an actor. Runs
  outside the actor system, uses DI-resolved `IChatClientProvider`.
- No new persistence events. Menu strings are transient state in
  `SkillIndexContextLayer` (volatile writes).
- No cross-actor communication. Each session actor reads the pre-built menu
  string independently.
