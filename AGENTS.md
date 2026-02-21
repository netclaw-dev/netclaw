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

## OpenSpec Workflow (MANDATORY)

**You MUST use OpenSpec skills for all planning and spec work.** Do not manually
create or edit OpenSpec artifacts (specs, changes, proposals, delta specs,
design docs, task files). Use the skills listed below.

### When Planning (new feature, capability, or spec change)

1. `/opsx-new` — create a new OpenSpec change
2. `/opsx-continue` — create next artifact in the change workflow
3. `/opsx-ff` — fast-forward: generate all remaining artifacts at once

### When Implementing (building code from a change)

4. `/opsx-apply` — implement tasks from an OpenSpec change

### When Finishing (syncing and archiving)

5. `/opsx-sync` — sync delta specs from a change to main specs
6. `/opsx-verify` — verify implementation matches change artifacts
7. `/opsx-archive` — archive a completed change

### Supporting Workflows

- `/opsx-explore` — think through ideas before creating a change
- `/opsx-onboard` — guided walkthrough of the full OpenSpec workflow
- `/opsx-bulk-archive` — archive multiple completed changes

**Hard rule:** If you need to create or modify files under `openspec/`, use the
appropriate skill above. The only exception is updating task checkboxes in
`openspec/changes/*/tasks.md` during RALPH iterations.

## Discovery Rules

Before coding a capability, discover in this order:

1. matching PRD in `docs/prd/`
2. matching engineering spec in `docs/spec/`
3. matching OpenSpec capability in `openspec/specs/`
4. active change plan in `openspec/changes/<name>/`

If planning and implementation artifacts conflict, fix planning artifacts first.
If discovery artifacts conflict with each other, update them before implementing.

## Universal Quality Bar

- secure-by-default behavior for gateway and tools
- no hidden bypasses around ACL/policy checks
- no north-star/deferred features in MVP without explicit PRD update
- actor boundaries remain transport-agnostic (pub/sub over direct transport asks)
- persistence types remain framework-owned and serialization-safe
- no new Slopwatch violations: run `/dotnet-skills:slopwatch` after code changes

## Post-Code Quality Check

After any code changes, run:

```bash
dotnet slopwatch analyze     # Detect reward hacking (new violations fail CI)
```

Slopwatch detects: disabled/skipped tests, suppressed warnings, empty catch
blocks, hardcoded values, TODO-as-done comments. Baseline is in
`.slopwatch/baseline.json` — existing entries are accepted, new violations
must be fixed or explicitly baselined with justification.

## Definition of Done

Done means all of the following are true:

- behavior aligns with PRD + spec
- acceptance criteria are testable and verified
- `dotnet slopwatch analyze` passes (no new violations)
- operational impact is documented (runbooks or CLI help)
- OpenSpec artifacts are updated or archived appropriately

## Continuous Improvement Rule

- If a workflow repeats twice, extract or refine a skill/workflow doc.
- Put volatile detail in repo-owned docs, not this constitution.
- Keep this file stable and high-signal.
