## ADDED Requirements

### Requirement: Automatic pre-turn skill auto-loading

The session system SHALL run deterministic skill auto-loading before each
user-facing model turn, after automatic memory recall and before dynamic
context layer injection. The auto-load pipeline SHALL tokenize the user
message, score it against enriched keywords in the skill registry, and inject
matched skill content as transient system messages.

#### Scenario: Skill auto-loaded on first matching turn

- **GIVEN** a skill's enriched keywords match the user message above threshold
- **WHEN** the turn pipeline prepares the model request
- **THEN** the session reads the skill's SKILL.md from disk
- **AND** caches the content in session-scoped state
- **AND** injects it as a transient system message tagged
  `[skill-auto-loaded: <name>]`
- **AND** logs `turn_skill_auto_load` with skill names and scores

#### Scenario: Cached skill re-injected on subsequent turns

- **GIVEN** a skill was auto-loaded on a previous turn in this session
- **WHEN** a new turn begins
- **THEN** the skill content is re-injected from the in-memory cache
- **AND** no disk read occurs for the previously loaded skill

#### Scenario: No match produces no injection

- **GIVEN** no skill's enriched keywords match the user message above threshold
- **WHEN** the turn pipeline prepares the model request
- **THEN** no skill auto-load injection occurs
- **AND** the compressed skill index remains available for LLM-driven loading

#### Scenario: Skill registry unavailable degrades safely

- **GIVEN** the `SkillRegistry` dependency is null (not configured)
- **WHEN** the turn pipeline runs
- **THEN** skill auto-loading is skipped entirely
- **AND** the turn proceeds normally with memory recall and context layers

#### Scenario: Skill file read failure skips that skill

- **GIVEN** a skill's enriched keywords match above threshold
- **WHEN** the skill's SKILL.md file cannot be read (`IOException`)
- **THEN** a warning is logged
- **AND** the skill is skipped
- **AND** other matched skills are still loaded

## MODIFIED Requirements

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach informed
by cross-SDK research (OpenAI, LangChain, Semantic Kernel, Anthropic, Google
ADK). Before and after compaction boundaries, the session SHALL emit
high-priority memory checkpoints into the durable memory queue instead of
performing a synchronous one-off memory flush that depends on the turn path
completing all curation work inline.

When compaction completes, the session SHALL clear all skill auto-load state
(loaded skill names and cached content) so that skill relevance is
re-evaluated on the next turn based on the current conversation context.

#### Scenario: Compaction threshold reached

- **GIVEN** `UsageDetails.InputTokenCount` exceeds `SessionConfig.CompactionTokenLimit`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` behavior state
- **AND** incoming messages are buffered during compaction

#### Scenario: Compaction boundary emits memory checkpoint

- **GIVEN** compaction is about to run or has just completed a summary reduction
- **WHEN** the compaction boundary is reached
- **THEN** the session enqueues a high-priority memory checkpoint for durable
  curation
- **AND** the user-facing session does not wait for background curation to
  finish

#### Scenario: Tiered compaction — tool result clearing first

- **GIVEN** compaction is triggered
- **WHEN** phase 1 runs
- **THEN** old tool results are replaced with placeholders
- **AND** the N most recent tool interactions are preserved in full
- **AND** if threshold is now satisfied, no summarization LLM call is made

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) did not bring context under threshold
- **WHEN** phase 3 runs
- **THEN** a structured summarization LLM call is made with domain-specific
  section headings (task overview, current state, decisions, pending actions)
- **AND** a `SessionCompacted` event is persisted
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** compaction runs
- **THEN** tool call/result pairs are never orphaned
- **AND** older tool interactions remain representable for checkpoint extraction
  and summarization

#### Scenario: Compaction clears skill auto-load state

- **GIVEN** one or more skills have been auto-loaded in the current session
- **WHEN** compaction completes and `SessionCompacted` is persisted
- **THEN** the loaded skill name set is cleared
- **AND** the cached skill content dictionary is cleared
- **AND** on the next turn, skill matching re-evaluates against the current
  user message
