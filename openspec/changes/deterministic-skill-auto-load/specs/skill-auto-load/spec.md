## ADDED Requirements

### Requirement: Shared text tokenizer

The system SHALL provide a shared `TextTokenizer` utility for deterministic
text tokenization with stopword removal and plural normalization. All
components performing token-level text matching (memory retrieval planning,
memory candidate selection, skill keyword matching) SHALL use this shared
utility.

#### Scenario: Tokenize user message with stopword removal

- **WHEN** a text string is tokenized via `TextTokenizer.Tokenize()`
- **THEN** the result contains lowercase alphanumeric tokens of length >= 2
- **AND** common English stopwords are excluded
- **AND** plural suffixes are normalized (e.g., "prices" → "price",
  "categories" → "category", "matches" → "match")

#### Scenario: Plural normalization preserves non-plural words

- **WHEN** a token ending in "s" is normalized via `NormalizePlural()`
- **THEN** words ending in "ss" (e.g., "class", "miss") are NOT modified
- **AND** words shorter than 4 characters are NOT modified

### Requirement: LLM sidecar keyword enrichment at scan time

The system SHALL generate comprehensive keyword lists from each skill's
SKILL.md content using a sidecar LLM call at scan time. Keywords SHALL bridge
developer-facing language in the skill content (e.g., "cite sources",
"factual claims") to user-facing language (e.g., "buy", "shop", "recommend").
Results SHALL be cached per skill version to avoid redundant LLM calls.

#### Scenario: Enrich keywords for a new skill version

- **WHEN** a skill is scanned and no cached keywords exist for its current
  version and content hash
- **THEN** the enrichment service calls the sidecar LLM
  (`ModelRole.Compaction`) with the skill's SKILL.md content
- **AND** parses the response into a tokenized keyword set
- **AND** caches the result to `~/.netclaw/cache/skill-keywords/` with the
  skill name, version, and content hash

#### Scenario: Use cached keywords on subsequent startup

- **WHEN** a skill is scanned and a cached keyword file exists with a matching
  content hash
- **THEN** the enrichment service loads keywords from the cache file
- **AND** does NOT call the sidecar LLM

#### Scenario: Sidecar unavailable at scan time

- **WHEN** the sidecar LLM call fails or times out during enrichment
- **THEN** the enrichment service falls back to tokenizing the skill's
  existing `Triggers` field and `Description` field
- **AND** stores the basic keyword set in the registry
- **AND** logs a warning indicating degraded enrichment
- **AND** retries enrichment on next startup or feed sync

#### Scenario: Feed sync triggers re-enrichment

- **WHEN** `SystemSkillSyncService` updates a skill's files on disk
- **THEN** the enrichment service detects the content hash change
- **AND** re-enriches the skill with a fresh sidecar call

### Requirement: Enriched keyword index in skill registry

The system SHALL maintain an in-memory keyword index in `SkillRegistry`
mapping skill names to enriched keyword token sets. Keywords SHALL NOT be
stored in skill files or injected into the LLM context window. Keywords SHALL
only be used for deterministic matching.

#### Scenario: Enrichment service populates the keyword index

- **WHEN** the enrichment service completes (from cache or sidecar)
- **THEN** `SkillRegistry` contains a keyword token set for each enriched skill
- **AND** the keyword set is accessible for matching queries

#### Scenario: Registry clear preserves re-enrichment path

- **WHEN** `SkillRegistry.Clear()` is called during feed sync
- **THEN** the enriched keyword index is also cleared
- **AND** the enrichment service re-populates it after re-scanning

### Requirement: Threshold-based keyword matching

The system SHALL score each skill's enriched keywords against user message
tokens using set intersection. A skill SHALL only be considered a match when
the token overlap count meets or exceeds a configurable threshold (default: 2).
Results SHALL be sorted by overlap score descending.

#### Scenario: Strong match auto-loads skill

- **GIVEN** a skill has enriched keywords including "buy", "price", "product"
- **WHEN** a user message tokenizes to include "buy" and "price"
- **THEN** the skill's overlap score is 2 (>= threshold)
- **AND** the skill is returned as a match

#### Scenario: Weak match does not auto-load

- **GIVEN** a skill has enriched keywords including "search", "find", "query"
- **WHEN** a user message tokenizes to include only "search"
- **THEN** the skill's overlap score is 1 (< threshold of 2)
- **AND** the skill is NOT returned as a match

#### Scenario: Multiple skills scored and ranked

- **GIVEN** two skills both have keywords matching the user message
- **WHEN** skill A has 4 keyword overlaps and skill B has 2
- **THEN** both skills are returned as matches
- **AND** skill A is ranked first (higher score)

#### Scenario: Already-loaded skills excluded from matching

- **GIVEN** a skill has been auto-loaded in the current session
- **WHEN** a subsequent user message matches the same skill's keywords
- **THEN** the skill is excluded from matching results
- **AND** its cached content continues to be injected from the session cache

#### Scenario: Max results cap respected

- **GIVEN** 5 skills all score above threshold
- **WHEN** matching runs with maxResults=3
- **THEN** only the top 3 skills by score are returned
