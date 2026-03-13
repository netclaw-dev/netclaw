## Context

Netclaw's skill system uses three-tier progressive disclosure: metadata
(compressed index in every LLM call) → instructions (full SKILL.md loaded on
demand) → references (sub-documents loaded when needed). The metadata→instructions
transition is LLM-driven: the agent sees a compressed index with "LOAD WHEN"
triggers and decides whether to `file_read` the skill.

This fails reliably. The LLM takes the shortest path — it goes straight to
action without loading the skill. The memory system solved the analogous problem
(what memories to recall) with deterministic retrieval planning: tokenize the
user message, infer facets and keywords, query the store, inject results as
transient system messages before the LLM runs. Skills need the same pattern,
adapted for a static registry rather than a dynamic store.

**Current components involved:**
- `SkillScanner` discovers skills at startup from `~/.netclaw/skills/`
- `SkillRegistry` holds `SkillEntry` records with name, description, triggers, file paths
- `SkillIndexContextLayer` injects the compressed index into every LLM call
- `SystemSkillSyncService` syncs system skills from CDN feed
- `LlmSessionActor.FireLlmCall()` orchestrates message assembly: history →
  memory recall → context layers → LLM invocation
- `DeterministicRetrievalPlanning` + `DeterministicCandidateSelector` provide
  the tokenizer and keyword matching patterns for memory recall
- `SessionSidecarRunner` provides the async LLM sidecar call pattern

## Goals / Non-Goals

**Goals:**
- Auto-load skills into the LLM context before it makes decisions, based on
  deterministic keyword matching against the user's message
- Generate comprehensive keyword lists from skill content using an LLM sidecar
  at scan time, bridging developer language to user language
- Handle skill overlap gracefully via threshold-based scoring (high confidence
  → auto-load, low confidence → fall through to compressed index)
- Track loaded skills per session so content is cached in memory (no repeat
  disk reads) and cleared on compaction for natural re-triggering
- Extract the duplicated tokenizer into a shared utility with plural
  normalization

**Non-Goals:**
- Replacing the compressed index — it remains as the LLM-driven fallback for
  ambiguous or low-confidence matches
- Semantic/embedding-based matching — token overlap is sufficient for a
  registry of ~7-15 skills
- Persisting auto-load state to the event journal — transient state that
  self-heals on actor recovery is simpler and sufficient
- Full Porter stemming — plural normalization plus explicit inflections in
  enriched keywords covers the common cases
- Auto-loading reference files — main SKILL.md is loaded; references remain
  progressive disclosure (the LLM follows explicit file paths once the skill
  is in context)

## Decisions

### D1: LLM sidecar for keyword enrichment (not deterministic extraction)

**Decision:** Use a sidecar LLM call at scan time to generate enriched keywords
from each skill's SKILL.md content.

**Why not deterministic extraction:** The skill's own text uses process language
("cite sources", "ensure factual claims include URLs") while users speak domain
language ("buy", "shop", "compare"). Pure tokenization of the skill content
produces zero overlap with typical user messages. The LLM bridges this gap by
understanding that a skill about "prices, products, reviews" is relevant when a
user says "buy" or "recommend."

**Why not hardcoded intent categories:** The memory system deliberately avoided
hardcoded facet-to-content mappings for the general case. Hardcoded intents
don't scale with new skills and create maintenance burden. The sidecar approach
generates keywords from the skill's own content — self-describing, no
hardcoding.

**Cost:** One sidecar call per skill per version. With ~7 system skills and
`ModelRole.Compaction` (cheapest model), this is negligible. Results are cached
to disk so the call doesn't repeat on subsequent startups.

**Alternatives considered:**
- *Hardcoded intent categories* — rejected for maintenance burden and scaling
- *Deterministic keyword extraction from content* — rejected because
  vocabulary gap between skill content and user language is too wide
- *Embedding-based matching* — rejected as overkill for <15 skills

### D2: Enriched keywords stored in SkillRegistry index (not in skill files)

**Decision:** Keywords live in `Dictionary<string, HashSet<string>>` inside
`SkillRegistry`, populated by the enrichment service. Cached to disk at
`~/.netclaw/cache/skill-keywords/{name}-{version}.json`.

**Why not modify SKILL.md:** Skill files are feed-managed artifacts with
versioned content hashes. Injecting generated content would break hash
verification and blur the line between authored and generated content.

**Why not inject into context window:** Enriched keywords are a search index,
not guidance for the LLM. Only the matched skill's actual SKILL.md content
enters the context window.

### D3: Threshold-based scoring (not boolean match)

**Decision:** A skill auto-loads only when the token overlap between the user
message and the skill's enriched keywords meets a threshold (default: 2).
Single-word overlap is ignored.

**Why:** Prevents false positives from common words. If memorizer-usage and
search-citation both have "search" in their keywords, a message containing
only "search" shouldn't auto-load either. But "buy a product" overlapping
with both "buy" and "product" in search-citation's keywords is a strong signal.

**Overlap handling:** When multiple skills score above threshold, all are loaded
(up to maxResults=3), sorted by score. The skill with the most keyword overlap
wins the highest injection position. This handles the case where two skills are
both legitimately relevant (e.g., travel query → search-citation for web search
+ another skill for preferences).

### D4: Transient per-session state (not persisted events)

**Decision:** Auto-load tracking uses in-memory `HashSet<string>` and
`Dictionary<string, string>` in `LlmSessionActor`. Not persisted to the
event journal.

**Why:** Skill auto-load state is cheap to reconstruct. On actor recovery,
the sets are empty. On the next `FireLlmCall`, trigger matching runs again
and re-loads any relevant skills. The only cost is one disk read per skill
on the first turn after recovery.

**Compaction behavior:** Both collections clear when `SessionCompacted` is
persisted. On the next turn, matching re-evaluates against the current user
message. If the topic has shifted, skills don't re-load (saving tokens). If
the topic persists, skills re-load naturally.

### D5: Injection position: after memory recall, before context layers

**Decision:** Auto-loaded skill content is injected as a transient system
message between `InjectAutomaticRecall` and `InjectDynamicContextLayers` in
`FireLlmCall()`.

**Message ordering:**
1. Persisted system prompt (SOUL.md + AGENTS.md + TOOLING.md)
2. Memory recall (transient, per-turn)
3. **Auto-loaded skills (transient, per-turn)** ← new
4. Context layers (tool index, skill index, time)
5. Conversation history

**Why this position:** Skills are behavioral guidance for the current turn —
higher priority than generic metadata (tool index, time) but lower than
user-specific context (recalled memories). The LLM sees the skill rules before
making any decisions.

### D6: Shared TextTokenizer (DRY refactoring)

**Decision:** Extract the tokenizer from `DeterministicRetrievalPlanning.cs`
(lines 25-149) into `TextTokenizer` in `Netclaw.Actors.Text` namespace. Add
plural normalization (`NormalizePlural`). Update both existing consumers.

**Plural rules:** Strip trailing `s`/`es`/`ies` with guards for edge cases
(don't strip `ss`, handle `ies→y`, handle sibilant `es`). Applied during
tokenization so both user message tokens and enriched keyword tokens are
normalized consistently.

### D7: Degradation when sidecar is unavailable

**Decision:** If the enrichment sidecar fails or is unavailable (no model
configured, timeout, first boot offline), fall back to tokenizing the skill's
existing `Triggers` field + `Description` field. This produces a basic keyword
set that won't bridge the vocabulary gap but is better than nothing.

**Recovery:** On next startup (or next feed sync), the enrichment service
retries. Successful results are cached, replacing the basic fallback.

## Risks / Trade-offs

**[R1] Enriched keywords may not cover all user phrasings**
→ Mitigation: The compressed index remains as LLM-driven fallback. Auto-loading
is additive — it catches the high-confidence cases deterministically, while the
existing system handles edge cases probabilistically.

**[R2] Sidecar LLM generates poor or noisy keywords**
→ Mitigation: Cache files are human-readable JSON. Bad keyword sets can be
manually reviewed and the cache file deleted to force regeneration. The prompt
should be specific about output format (one keyword per line, lowercase).

**[R3] Token overlap scoring may produce false positives on broad keywords**
→ Mitigation: Threshold of 2 requires multiple keyword matches. Single-word
overlap (which would be most false positives) is ignored. Logging includes
skill names and scores so false positives are observable in daemon logs.

**[R4] Compaction clears auto-load state, re-triggering a disk read**
→ Mitigation: Disk reads for SKILL.md files are <1ms (files are 2-4KB). This
only happens once per compaction event, which is itself infrequent.

**[R5] Thread safety of SkillRegistry enriched keywords**
→ Mitigation: `SetEnrichedKeywords` is called from the enrichment service
(background thread) while `MatchByKeywords` is called from session actor
threads. The dictionary write completes before the service signals readiness.
If needed, use `ConcurrentDictionary` or swap-on-write pattern.

**[R6] Feed sync mid-session updates enriched keywords**
→ Mitigation: `SystemSkillSyncService` re-scans skills and triggers
re-enrichment. Session-cached content in `_autoLoadedSkillContent` may be
stale until compaction clears it. Acceptable since skill content changes are
rare and compaction naturally resets.

## Actor Boundaries and Persistence Implications

- **SkillTriggerEnrichmentService** is an `IHostedService`, not an actor. It
  runs outside the actor system, uses DI-resolved `IChatClientProvider`, and
  writes to `SkillRegistry` (shared singleton). No actor messages involved.
- **LlmSessionActor** gains transient state (`_autoLoadedSkills`,
  `_autoLoadedSkillContent`) and a new injection step. No new persistence
  events. No new message types. The `SkillRegistry` dependency is resolved
  from DI via the existing constructor injection pattern.
- **No cross-actor communication** for skill auto-loading. Each session actor
  independently queries the shared `SkillRegistry` and maintains its own
  loaded-skill state. No coordination needed.

## Failure Modes and Recovery

| Failure | Behavior | Recovery |
|---------|----------|----------|
| Enrichment sidecar timeout | Fall back to basic trigger/description tokens | Retries on next startup |
| Enrichment cache file corrupt | Treated as cache miss, re-enriches | Automatic on next scan |
| Skill file missing at load time | `IOException` caught, skill skipped | Logged, other skills still load |
| SkillRegistry cleared during feed sync | Enriched keywords cleared | Re-enrichment triggered by sync service |
| Actor recovery (crash/restart) | Auto-load state empty | Trigger matching on next turn re-loads |
| Compaction | Auto-load state cleared | Trigger matching on next turn re-evaluates |
