# Netclaw Agent Constitution

This file is the repository's stable agent constitution.
Keep it small. Keep it durable. Keep it routing-focused.

## Authority and Scope

- You are authorized to plan, design, implement, test, and document Netclaw.
- Default to smallest safe change that advances MVP.
- Prefer explicit tradeoffs over hidden complexity.

## Current Product Direction

- Netclaw is a Slack-connected homelab assistant built on Akka.Agents.
- MVP is single process, actor-driven, and persistence-backed.
- Session identity is Slack thread: `{channelId}/{threadTs}`.
- Security posture is default deny.

Read first:

- `PROJECT_CONTEXT.md`
- `TOOLING.md`
- `IMPLEMENTATION_PLAN.md`
- `docs/prd/README.md`
- `.opencode/skills/netclaw-*/SKILL.md`
- `.claude/skills/ralph-*.md`
- relevant `openspec/specs/*/spec.md`

## Required Task Routing

Use these modes based on requested outcome.

### MODE=planning

Use when producing PRDs, specifications, risk analysis, IA, mockups, or
execution plans.

Expected outputs:

- updates in `docs/prd/`, `docs/spec/`, `docs/ui/`
- OpenSpec changes and spec deltas in `openspec/`
- explicit traceability to PRD IDs

### MODE=build

Use when implementing production code, tests, and runtime wiring.

Expected outputs:

- code changes with validation steps
- matching spec updates when behavior changes
- no undocumented behavior drift

## Discovery Rules

Before coding a capability, discover in this order:

1. matching PRD in `docs/prd/`
2. matching engineering spec in `docs/spec/`
3. matching OpenSpec capability in `openspec/specs/`
4. active change plan in `openspec/changes/<name>/`

If those artifacts conflict, update planning artifacts first, then implement.

## Universal Quality Bar

- secure-by-default behavior for gateway and tools
- no hidden bypasses around ACL/policy checks
- no north-star/deferred features in MVP without explicit PRD update
- actor boundaries remain transport-agnostic (pub/sub over direct transport asks)
- persistence types remain framework-owned and serialization-safe

## Definition of Done

Done means all of the following are true:

- behavior aligns with PRD + spec
- acceptance criteria are testable and verified
- operational impact is documented (runbooks or CLI help)
- OpenSpec artifacts are updated or archived appropriately

## Continuous Improvement Rule

- If a workflow repeats twice, extract or refine a skill/workflow doc.
- Put volatile detail in repo-owned docs, not this constitution.
- Keep this file stable and high-signal.
