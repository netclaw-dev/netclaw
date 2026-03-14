## Why

The skill system's progressive disclosure relies on the LLM voluntarily loading
skills from a compressed index. In practice, the LLM skips this step in the
majority of cases — Vercel's evals measured 56% non-invocation. On 2026-03-13
a user asked about buying a CO2 regulator; the agent called `web_search` but
returned product recommendations with zero source URLs because the
`search-citation` skill was never loaded. The skill guidance ("No URL, no fact")
was never in context.

The system needs a deterministic, pre-LLM skill activation mechanism that
extracts user intent from the message and matches it against skill relevance —
without relying on the LLM to opt in or skill authors to write perfect triggers.

## What Changes

- **LLM sidecar enrichment at scan time**: after skills are scanned, a sidecar
  LLM call generates comprehensive keyword lists from each skill's content
  (bridging developer language like "cite sources" to user language like
  "buy/shop/compare"). Results are cached per skill version.
- **Enriched keyword search index in SkillRegistry**: keywords live in an
  in-memory index used for matching, not in skill files or the context window.
- **Threshold-based keyword matching in FireLlmCall**: before each LLM call,
  user message tokens are scored against enriched keywords. Only high-confidence
  matches (multiple keyword overlap) trigger auto-loading. Low-confidence and
  ambiguous matches fall through to the existing compressed index for LLM-driven
  loading.
- **Per-session auto-load tracking**: loaded skills are cached in-memory per
  session (no disk read on subsequent turns). State clears on compaction so
  skills re-trigger naturally if still contextually relevant.
- **Shared TextTokenizer utility**: extract the duplicated tokenizer from
  `DeterministicRetrievalPlanning` and `DeterministicCandidateSelector` into a
  shared utility with plural normalization.

## Capabilities

### New Capabilities
- `skill-auto-load`: Deterministic pre-LLM skill activation via enriched
  keyword matching — covers scan-time enrichment, keyword index, threshold
  scoring, session-scoped auto-load state, and compaction-aware lifecycle.

### Modified Capabilities
- `netclaw-session`: Session actor gains skill auto-load injection step in
  `FireLlmCall()` between memory recall and context layers, plus compaction
  clearing of auto-load state.

## Impact

- **Code**: `SkillRegistry` (new index + matching method),
  `LlmSessionActor` (new injection step + transient state),
  new `SkillTriggerEnrichmentService` hosted service,
  new `TextTokenizer` shared utility, DRY refactor of existing tokenizer copies.
- **Dependencies**: No new external dependencies. Uses existing
  `SessionSidecarRunner` and `IChatClientProvider` (Compaction role).
- **Runtime**: One LLM sidecar call per skill per version at scan time (cached).
  Per-turn cost is token overlap scoring (~70 set intersections, sub-millisecond)
  plus re-injection of cached skill content (~3KB per loaded skill).
- **Persistence**: No new persistence events. Auto-load state is transient —
  empty on actor recovery, rebuilt by trigger matching on next turn.
- **Cache**: New `~/.netclaw/cache/skill-keywords/` directory for enrichment
  cache files (JSON, keyed by skill name + version + content hash).
