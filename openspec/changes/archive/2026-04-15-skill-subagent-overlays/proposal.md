## Why

Issue #661 exposed an execution-model gap: slash-invoked skills always inline their body into the main session, so there is no deterministic way to route a skill to a dedicated user-facing subagent with strict isolation defaults. We need a declarative, fail-loud routing contract that preserves Netclaw's default-deny posture and prevents silent fallback behavior.

## What Changes

- Add declarative skill routing metadata: `metadata.subagent: <name>` in skill frontmatter, aligned with AgentSkills metadata extension conventions.
- Introduce deterministic activation precedence for all first-party skill activation entry points: when `metadata.subagent` is present and valid, route to that subagent path before inline skill-body injection.
- Define routed execution semantics: skill body becomes an additive subagent system-prompt overlay (specialization layer), not main-session user runtime context.
- Enforce dispatch-time validation for routed metadata on every activation request.
- Keep routed tool authorization audience-governed for MVP; no new skill-level tool gate in this change.
- Enforce isolation defaults for routed subagent workers: they do not inherit the main session identity prompt stack by default and do not auto-load repo-local `AGENTS.md` by default.
- Enforce fail-loud semantics with no silent fallback to inline execution for unknown subagent targets, internal-only subagent targets, and malformed routed metadata; routed failures must be user-visible and include remediation guidance.

## Capabilities

### New Capabilities

- `skill-execution-routing`: Defines deterministic, metadata-driven routing from skill activation to subagent execution, including overlay semantics and failure behavior.

### Modified Capabilities

- `slash-command-dispatch`: Adds deterministic routing precedence for `metadata.subagent` and extends frontmatter parsing/validation behavior for slash-invoked skills.
- `netclaw-subagents`: Adds explicit isolation and visibility constraints for routed skill overlays, including user-facing-target validation and prompt-stack boundaries.

## Impact

### Affected code and systems

- Skill parsing and registry surfaces that materialize frontmatter metadata.
- Slash-command dispatch flow in session handling, including scheduled slash payload handling.
- Any other first-party skill activation entry points that execute skills (for example tool-driven activation paths).
- Subagent registry/spawner validation and prompt assembly for routed execution.
- Subagent prompt construction boundary and context-layer composition behavior.

### APIs and behavior

- No external API break expected; this is a behavioral contract change in skill activation routing.
- **BREAKING (internal behavior):** skills with valid `metadata.subagent` no longer follow inline injection path.

### Security and operations

- Fail-loud behavior replaces implicit fallback, improving observability and reducing hidden privilege/behavior drift.
- Internal-only subagent targets are explicitly denied for user-facing routed skill execution.
- Operator-visible deterministic errors are required for malformed metadata and unknown targets, including remediation steps (fix/remove `metadata.subagent` or add the referenced subagent definition).

### In scope for MVP

- Metadata parsing/validation for `metadata.subagent`.
- Dispatch-time metadata validation on each activation request.
- Deterministic routing precedence and routed overlay semantics.
- Isolation defaults for routed subagent workers.
- Failure semantics and regression tests for no-fallback guarantees.
- System-skill documentation updates for skill authoring guidance.

### Out of scope for MVP

- Automatic migration of existing skill metadata to add `metadata.subagent`.
- Dynamic runtime policy that allows fallback from routed path to inline path.
- Skill-level tool-scope intersection policy (e.g., audience policy ∩ subagent tools ∩ `allowed-tools`).
- Broad redesign of subagent prompt assembly beyond routed skill overlay and stated isolation defaults.

### Source PRDs

- `PRD-001` (MVP architecture and deterministic session behavior)
- `PRD-002` (default-deny, fail-closed security posture)
- `PRD-007` (agent behavior shaping and operational prompt governance)
