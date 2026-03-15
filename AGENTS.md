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
- use `TimeProvider` (not `DateTime.UtcNow` / `DateTimeOffset.UtcNow`) so time
  can be virtualized in tests. Inject `TimeProvider` via DI; default to
  `TimeProvider.System` in production. Standardize on `DateTimeOffset`, not
  `DateTime`. Usage: `_timeProvider.GetUtcNow()` returns `DateTimeOffset`,
  `.ToUnixTimeMilliseconds()` for persistence timestamps.
- **NEVER add implicit conversions to/from primitive types on value objects.**
  Value objects exist to prevent accidental misuse — an implicit conversion back
  to the primitive defeats the purpose. Use `.Value` for explicit access and
  explicit casts where truly needed. If a value object can silently become a
  string, it provides no more safety than a raw string.

## Testing Guidelines

- Do not write tests for trivial code — string formatting, simple concatenation,
  constructor assignment, and other zero-logic paths are not worth testing.
- Tests should exercise meaningful behavior: state transitions, error handling,
  serialization round-trips, routing decisions, integration boundaries.
- If the test is just asserting that `$"{a}/{b}"` equals `"a/b"`, delete it.
- Prefer fewer tests that cover real behavior over many tests that pad coverage.
- **NEVER use `Thread.Sleep` or `Task.Delay` in tests to wait for conditions.**
  This is a design smell, not just a test smell — if you need a sleep to make a
  test pass, the production code lacks a proper synchronization signal. Fix the
  design:
  - Add request/response acks (e.g., `Ask<CommandAck>`) so callers know a state
    transition has occurred before proceeding.
  - Use Akka.TestKit's `AwaitAssertAsync` for polling assertions on async state.
  - `Task.Delay` in fake/mock services to simulate latency is acceptable only in
    the fake itself, never in test orchestration logic.

## Post-Code Quality Check

After any code changes, run:

```bash
dotnet slopwatch analyze     # Detect reward hacking (new violations fail CI)
```

Slopwatch detects: disabled/skipped tests, suppressed warnings, empty catch
blocks, hardcoded values, TODO-as-done comments. Baseline is in
`.slopwatch/baseline.json` — existing entries are accepted, new violations
must be fixed or explicitly baselined with justification.

## System Skills Sync Rule

System skills in `feeds/skills/.system/files/` are the agent's operational
guidance — they tell the running agent how to use features. When you change a
feature area, the corresponding skill **must** be updated in the same PR.

| Feature area changed | Skill to update |
|----------------------|-----------------|
| Identity files, SOUL/AGENTS/TOOLING paths, progressive disclosure | `netclaw-identity` |
| Memory provider routing, SQLite memory tools, general memory guidance | `netclaw-memory` |
| Config format, daemon health, logs, MCP wiring, diagnostics CLI, doctor | `netclaw-diagnostics` |
| Skill file format, discovery, authoring workflow | `skill-authoring` |
| Tool definitions, CLI commands, grant categories, search_tools, scheduling tools | `netclaw-manual` |
| Search tool behavior, citation policy, web_search/web_fetch usage guidance | `search-citation` |

**Workflow:**
1. Edit the skill source at `feeds/skills/.system/files/{name}/{version}.md`
2. Bump the version (new file, e.g. `1.1.0.md`) — do not overwrite old versions
3. Run `./feeds/scripts/generate-skill-manifest.sh` to rebuild `manifest.json`
4. Update the embedded copy in `src/Netclaw.Daemon/BuiltInSkills/` to match
   (this is the offline bootstrap — must stay in sync with the latest feed version)
5. Include all four changes (skill file, manifest, embedded copy) in the same commit

If a new feature area needs agent guidance, create a new skill file and add a
row to this table.

## Definition of Done

Done means all of the following are true:

- behavior aligns with PRD + spec
- acceptance criteria are testable and verified
- `dotnet slopwatch analyze` passes (no new violations)
- operational impact is documented (runbooks or CLI help)
- OpenSpec artifacts are updated or archived appropriately
- system skills updated if a mapped feature area was changed (see table above)

## Agent Guidance: dotnet-skills

IMPORTANT: Prefer retrieval-led reasoning over pretraining for any .NET work.
Workflow: skim repo patterns -> consult dotnet-skills by name -> implement
smallest-change -> note conflicts.

Routing (invoke by name):

- Akka.NET: akka-best-practices, akka-hosting-actor-patterns, akka-testing-patterns
- C# / code quality: csharp-coding-standards, csharp-concurrency-patterns,
  csharp-api-design, csharp-type-design-performance
- DI / config: microsoft-extensions-dependency-injection, microsoft-extensions-configuration
- Serialization: serialization
- Testing: akka-testing-patterns, snapshot-testing
- Project structure: project-structure, package-management

Quality gates (use when applicable):

- dotnet-skills:slopwatch — after substantial new/refactor/LLM-authored code
- dotnet-skills:crap-analysis — after tests added/changed in complex code

Specialist agents:

- akka-net-specialist, dotnet-concurrency-specialist, dotnet-performance-analyst,
  dotnet-benchmark-designer

## Continuous Improvement Rule

- If a workflow repeats twice, extract or refine a skill/workflow doc.
- Put volatile detail in repo-owned docs, not this constitution.
- Keep this file stable and high-signal.
