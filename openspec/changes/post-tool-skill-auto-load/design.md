## Context

Netclaw already has deterministic pre-turn skill auto-loading based on the last
user message. That improves first-call behavior, but it does not guarantee that
the follow-up LLM call after tool execution sees the right skill guidance.

The gap matters most for search-driven turns. A user message may be too vague to
trigger `search-citation`, yet the model can still call `web_search` or
`web_fetch`, receive current facts, and answer without inline links. The turn is
now in a different state: the assistant is no longer deciding whether to search;
it is composing a response from verified tool output and needs the citation
rules in context.

Current relevant components:
- `LlmSessionActor.FireLlmCall()` handles both the first model call and all
  post-tool follow-up calls.
- `ToolExecutionCompleted` appends tool results and immediately triggers another
  `FireLlmCall()`.
- `ResolveAndInjectAutoLoadedSkills()` currently evaluates only user-message
  keyword matches and re-injects cached skills.
- `ToolRegistry` owns tool registration and is the natural place to declare
  tool-level metadata.
- `search-citation` is the system skill that governs search-derived factual
  claims and inline source links.

This change is intentionally small: add deterministic tool-to-skill routing for
the post-tool phase without replacing the existing pre-turn matcher.

## Goals / Non-Goals

**Goals:**
- Ensure follow-up model calls after tool execution can auto-load required skill
  overlays even when the original user message did not trigger them.
- Keep the mechanism deterministic and explainable: tool name -> required skill
  list, with explicit logs.
- Reuse existing per-session skill caching, compaction clearing, and disk-read
  safeguards.
- Limit MVP scope to explicit first-party mappings, starting with
  `web_search`/`web_fetch` -> `search-citation`.

**Non-Goals:**
- Semantic inference from arbitrary tool result text.
- Auto-loading skills for every MCP tool or every discovered tool in this
  change.
- Changing the keyword enrichment service or pre-turn threshold scoring.
- Persisting post-tool skill state in the journal.

## Decisions

### D1: Declare post-tool skill dependencies in tool metadata

**Decision:** extend tool registration metadata so a tool can declare one or
more required skills for the post-tool answer phase.

**Why:** the dependency belongs to the tool contract, not to actor-local
hardcoded conditionals. `web_search` and `web_fetch` know they produce
verifiable facts that require citation guidance.

**Alternatives considered:**
- *Hardcode tool-name checks in `LlmSessionActor`* - rejected because it hides
  routing knowledge in the session engine and scales poorly.
- *Infer skills from tool result text* - rejected because it is less
  deterministic and harder to test.

### D2: Resolve tool-required skills only on post-tool follow-up calls

**Decision:** when `ToolExecutionCompleted` fires, the session captures any
  required skills from the executed tools and resolves them before the next
  `FireLlmCall()`.

**Why:** this keeps the existing pre-turn behavior intact and targets the exact
moment where the missing guidance matters: after tool results exist and before
the assistant writes the user-facing answer.

**Alternatives considered:**
- *Always evaluate tool-required skills on every call* - rejected because it
  adds needless work and blurs the distinction between user-intent and
  post-tool phases.

### D3: Reuse the existing auto-load cache and injection path

**Decision:** tool-triggered loads populate the same `_autoLoadedSkills` and
`_autoLoadedSkillContent` collections used by keyword-triggered loads.

**Why:** there should be one session-scoped skill cache, one disk-read path, and
one compaction reset path. The injection output can stay a transient system
message tagged per skill, with logging carrying the trigger reason.

**Alternatives considered:**
- *Separate cache for tool-triggered skills* - rejected because it duplicates
  lifecycle logic and complicates compaction behavior.

### D4: Missing or unreadable skills degrade safely

**Decision:** if a mapped skill is missing from the registry or its file cannot
be read, log a warning and continue the turn without that overlay.

**Why:** search results still exist and the session must not stall on guidance
loading. The failure should be observable but non-fatal.

### D5: Observability distinguishes user-triggered vs post-tool-triggered loads

**Decision:** extend `turn_skill_auto_load` logging with a reason/source field so
operators can tell whether a skill came from user-intent matching or tool
execution.

**Why:** the behavior is subtle and needs daemon-log proof during validation.

## Risks / Trade-offs

- **[R1] Tool metadata drifts from actual skill expectations** -> Mitigation:
  start with a tiny explicit mapping set and require matching system skill
  updates in the same PR.
- **[R2] Broad mappings over-inject skills on benign turns** -> Mitigation:
  scope MVP to `web_search` and `web_fetch`, where citation rules are almost
  always desirable after current-data retrieval.
- **[R3] Follow-up calls after repeated empty responses could re-run the same
  load work** -> Mitigation: reuse the session cache so subsequent calls only
  re-inject cached content, not re-read disk.
- **[R4] Missing registry entries hide operator mistakes** -> Mitigation:
  structured warnings identify the tool name and missing skill so feed-sync or
  packaging issues are diagnosable.

## Migration Plan

1. Add tool metadata support for post-tool skill dependencies.
2. Register `search-citation` as a dependency of `web_search` and `web_fetch`.
3. Update `LlmSessionActor` to collect and resolve required skills after tool
   execution and before the follow-up call.
4. Update `feeds/skills/.system/files/search-citation/SKILL.md` and bump its
   version so published guidance reflects the enforced behavior.
5. Validate with integration tests for search-driven turns and manual daemon-log
   verification.

Rollback is low risk: remove the metadata mapping and session hook, leaving the
existing pre-turn auto-load path intact.

## Open Questions

- None for MVP. Future work can decide whether MCP tool catalogs should be able
  to declare the same metadata once post-tool routing proves useful.

## Actor Boundaries and Persistence Implications

- `ToolRegistry` remains the shared registry for tool definitions and gains
  metadata about post-tool skill dependencies.
- `LlmSessionActor` gains transient per-turn state for pending post-tool skill
  requirements, but no new actor messages or persistence events are needed.
- Auto-loaded skill content remains transient session state and continues to be
  cleared on compaction and reconstructed after recovery.

## Failure Modes and Recovery

| Failure | Behavior | Recovery |
|---------|----------|----------|
| Tool declares unknown skill | Warning log, turn continues without that overlay | Fix skill registration/feed sync, next turn picks it up |
| Skill file read fails | Warning log, turn continues | Restore file; cache repopulates on next eligible turn |
| Repeated post-tool follow-up call in same turn | Cached skill is re-injected without disk re-read | Automatic |
| Actor restart before next turn | Pending post-tool requirements are lost | Next tool-using turn re-resolves dependencies |
