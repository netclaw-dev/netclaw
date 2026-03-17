Source PRDs: `PRD-001`, `PRD-007`

## Why

Deterministic skill auto-loading currently keys off the user's message before the
first model call. That misses an important case: once a turn has already used
`web_search` or `web_fetch`, the follow-up model call can still answer with
uncited, tool-derived facts if the original user phrasing did not cross the
auto-load threshold for `search-citation`.

## What Changes

- Add deterministic post-tool skill activation for follow-up model calls after
  tool execution completes.
- Let tools declare required skill overlays for their post-tool answer path;
  MVP scope starts with `web_search` and `web_fetch` requiring
  `search-citation`.
- Reuse the session's existing skill cache and compaction reset behavior so
  tool-triggered skill loads do not create a second context mechanism.
- Extend observability so logs show whether a skill was loaded from user-intent
  matching or from post-tool requirements.
- Update the `search-citation` system skill in the same implementation cycle so
  operator guidance stays aligned with the enforced post-search behavior.

In scope for MVP: deterministic, tool-name-based skill loading on post-tool
follow-up calls for explicitly mapped first-party tools. Out of scope: parsing
tool result text for semantic skill inference, blanket MCP-wide auto-loading,
or changing the existing pre-turn keyword matcher.

## Capabilities

### New Capabilities
- `post-tool-skill-routing`: tool-declared skill dependencies that activate on
  post-tool follow-up turns before the assistant composes a final answer.

### Modified Capabilities
- `netclaw-session`: the session turn pipeline injects tool-required skills on
  follow-up LLM calls after tool execution, using the existing session-scoped
  skill cache and compaction lifecycle.

## Impact

- **Code:** `LlmSessionActor`, `ToolRegistry`/tool metadata, search tool
  registrations, integration tests around multi-step tool turns, and system
  skill sync tests.
- **Operational:** daemon logs gain explicit post-tool auto-load reasons so
  operators can verify why citation guidance appeared on a turn.
- **Security:** no grants expand and no hidden bypass is introduced; this change
  only tightens post-search answer quality by forcing existing guidance into
  context after verified tool usage.
- **Dependencies:** no new external dependencies or persistence schema changes.
