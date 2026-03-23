# skill-index-compression Specification

## Purpose

Define the compressed skill index format, LLM-generated trigger phrases,
audience-aware filtering, and trust-tier visibility rules for the skill
discovery system.

## Requirements

### Requirement: Compressed pipe-delimited index format

The system SHALL generate a compressed skill index using pipe-delimited format
grouped by category. Each entry SHALL include the skill name and a short
trigger phrase. The index SHALL reference the `skill_load` tool for loading.

#### Scenario: Index generated from registered skills

- **GIVEN** 7 skills are registered across categories `.system` and root
- **WHEN** the index is generated
- **THEN** the output uses pipe-delimited format with category groupings
- **AND** total token count is under 500 tokens
- **AND** each skill entry includes name and trigger phrase

#### Scenario: Index references skill_load tool

- **WHEN** the compressed index is generated
- **THEN** the header references `skill_load(name)` as the loading mechanism
- **AND** no `file_read` paths appear in the index

#### Scenario: Skills grouped by category

- **GIVEN** skills in `.system/` category and root-level skills
- **WHEN** the index is generated
- **THEN** skills are grouped by their `Category` property
- **AND** each category appears as a pipe-prefixed line

### Requirement: LLM sidecar trigger phrase generation

The system SHALL use an LLM sidecar call at scan time to generate a short
(5-15 word) trigger phrase per skill. Trigger phrases SHALL bridge operator
language to user language. Results SHALL be cached to disk by skill name and
version.

#### Scenario: Trigger phrase generated for new skill

- **GIVEN** a skill has no cached trigger phrase
- **WHEN** the enrichment service processes the skill
- **THEN** an LLM sidecar call generates a trigger phrase
- **AND** the result is cached at `~/.netclaw/cache/skill-index/{name}-{version}.json`

#### Scenario: Cached trigger phrase reused on restart

- **GIVEN** a cached trigger phrase exists for skill name+version
- **WHEN** the enrichment service processes the skill
- **THEN** the cached phrase is used without an LLM call

#### Scenario: Cache invalidated on version change

- **GIVEN** a cached trigger phrase exists for `search-citation` version `0.6.0`
- **WHEN** the skill is updated to version `0.7.0`
- **THEN** the old cache entry is not used
- **AND** a new LLM sidecar call generates a fresh trigger phrase

#### Scenario: Fallback when sidecar unavailable

- **GIVEN** no LLM model is configured or the sidecar times out
- **WHEN** the enrichment service processes a skill
- **THEN** the first 60 characters of the `Description` field are used as the trigger phrase
- **AND** the fallback is NOT cached to disk (retry on next startup)

### Requirement: Audience-aware skill filtering

The system SHALL filter skills from the index based on the session's effective
trust audience and available tool set. Skills whose required tools are not
available SHALL be excluded.

#### Scenario: Public audience excludes shell-requiring skill

- **GIVEN** a skill has `allowed-tools: shell_execute`
- **AND** the session has `TrustAudience.Public` with no shell access
- **WHEN** the index is generated for this audience
- **THEN** the skill does not appear in the index

#### Scenario: Personal audience sees all skills

- **GIVEN** skills at System, Operator, and Agent trust tiers
- **AND** the session has `TrustAudience.Personal` with all tools available
- **WHEN** the index is generated for this audience
- **THEN** all skills appear in the index

#### Scenario: Skill without allowed-tools is always visible

- **GIVEN** a skill has no `allowed-tools` declared in frontmatter
- **WHEN** the index is generated for any audience
- **THEN** the skill appears (no tool gating applies)

### Requirement: Trust-tier visibility rules

The system SHALL enforce trust-tier-based visibility in the skill index.
Skills cannot widen their visibility beyond their tier default.

#### Scenario: Community skill hidden from Public audience

- **GIVEN** a skill with `SkillTrustTier.Community`
- **AND** the session has `TrustAudience.Public`
- **WHEN** the index is generated
- **THEN** the skill does not appear

#### Scenario: Community skill visible to Team audience

- **GIVEN** a skill with `SkillTrustTier.Community`
- **AND** the session has `TrustAudience.Team`
- **WHEN** the index is generated
- **THEN** the skill appears

#### Scenario: External skill visible only to Personal

- **GIVEN** a skill with `SkillTrustTier.External`
- **AND** the session has `TrustAudience.Team`
- **WHEN** the index is generated
- **THEN** the skill does not appear

### Requirement: Per-audience menu pre-generation

The system SHALL pre-generate compressed menus for each trust audience value
at scan time. Session startup SHALL select the appropriate pre-built menu.

#### Scenario: Menus rebuilt after feed sync

- **GIVEN** the skill registry is cleared and re-populated after a feed sync
- **WHEN** the enrichment service completes
- **THEN** per-audience menus are regenerated
- **AND** subsequent sessions see the updated menus

#### Scenario: Session selects menu by audience

- **GIVEN** pre-built menus exist for Public, Team, and Personal audiences
- **WHEN** a new session starts with `TrustAudience.Team`
- **THEN** the Team menu is injected into the system prompt

### Requirement: DisableModelInvocation index exclusion

Skills with `disable-model-invocation: true` in frontmatter SHALL be excluded
from the compressed index. They remain invokable via slash commands but the
LLM does not see them in the skill list.

#### Scenario: Disable-model-invocation skill excluded from index

- **GIVEN** a skill has `disable-model-invocation: true`
- **WHEN** the compressed index is generated
- **THEN** the skill does not appear in the index
- **AND** the skill remains available via slash-command dispatch
