## ADDED Requirements

### Requirement: Post-tool skill auto-loading before follow-up model calls

The session system SHALL resolve and inject tool-required skills before any
follow-up model call triggered by completed tool execution. Tool-required skills
SHALL share the same session-scoped cache and transient system-message
injection path used by other auto-loaded skills.

#### Scenario: Follow-up call after search loads citation skill
- **GIVEN** a turn completed `web_search` or `web_fetch`
- **AND** `search-citation` is not yet loaded in the current session
- **WHEN** the session prepares the follow-up LLM call with tool results in
  context
- **THEN** the session loads `search-citation` before the model call
- **AND** injects it as a transient system message in that follow-up request

#### Scenario: Cached tool-required skill is re-injected without disk read
- **GIVEN** a tool-required skill was already loaded earlier in the session
- **WHEN** another eligible post-tool follow-up call occurs
- **THEN** the skill content is re-injected from the in-memory cache
- **AND** the session does not re-read the skill file from disk

#### Scenario: Post-tool nudge retains loaded skill context
- **GIVEN** a tool-required skill was loaded for a follow-up call
- **AND** the model returns an empty post-tool response that triggers a nudge
- **WHEN** the session issues the nudged follow-up call in the same turn
- **THEN** the already-loaded skill remains injected from cache

#### Scenario: Logs show post-tool auto-load source
- **GIVEN** one or more skills were loaded because of completed tool execution
- **WHEN** the session logs skill auto-load activity
- **THEN** the log entry includes that the load reason was `post-tool`
- **AND** the entry lists the triggering tools and loaded skill names
