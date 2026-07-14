# Netclaw Agent Constitution

This file is the repository's stable agent constitution.
Keep it small. Keep it durable. Keep it routing-focused.

## Authority and Scope

- You are authorized to plan, design, implement, test, and document Netclaw.
- Default to smallest safe change that advances MVP.
- Prefer explicit tradeoffs over hidden complexity.

## Current Product Direction

- Netclaw is an open-source, self-hosted autonomous operations agent built on Akka.Agents.
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

## OpenSpec Workflow

Use OpenSpec skills for substantial planning and spec work — new features,
capability additions, or changes that touch multiple components. Skip it for
targeted bug fixes, small refactors, and changes where scope is already clear.
Do not manually create or edit OpenSpec artifacts (specs, changes, proposals,
delta specs, design docs, task files); use the skills below instead.

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

If you do need to create or modify files under `openspec/`, use the appropriate
skill above rather than editing them directly. The only exception is updating
task checkboxes in `openspec/changes/*/tasks.md` during RALPH iterations.

## Discovery Rules

Before coding a capability, discover in this order:

1. active task in `IMPLEMENTATION_PLAN.md`
2. matching PRD in `docs/prd/`
3. matching engineering spec in `docs/spec/`
4. matching OpenSpec capability in `openspec/specs/`
5. active change plan in `openspec/changes/<name>/`

If planning and implementation artifacts conflict, fix planning artifacts first.
If discovery artifacts conflict with each other, update them before implementing.

## Cross-Boundary Contract Rule

When a change writes data consumed by another subsystem, identify the consumer
before implementation and verify the producer emits the consumer's canonical
representation. This applies to config editors, persistence records, actor
messages, protocol payloads, tool schemas, and security policy inputs.

For configuration changes, tests must prove both:

- invalid or unresolved values are rejected before persistence
- persisted values match what runtime ACL/routing/startup code expects

Do not treat UI-level save success or schema validity as sufficient when runtime
behavior depends on provider IDs, canonical names, permissions, or security
policy keys.

## Automation Floor

Recent regressions define mandatory automated proof classes. TUI text input must
have headless typed-key coverage and native smoke coverage for critical flows.
Dynamic validation must have fake-failure tests proving save is blocked before
persistence. Legacy/new config paradigm changes must have load/round-trip tests
from the old shape to the runtime-consumed shape. Human manual testing is a
last-mile confidence check, not a substitute for these gates.

## Configuration Schema Sync Rule

When adding or changing properties on any `*Config` type in `Netclaw.Configuration`,
update `src/Netclaw.Configuration/Schemas/netclaw-config.v1.schema.json` in the same PR.
The schema uses `"additionalProperties": false` throughout — any new property that is
missing from the schema will be rejected by `ConfigSchemaDoctorCheck` at runtime.

**Migration-friendly schema changes:** `netclaw doctor --fix` uses `SchemaFixResolver` to
auto-fix common schema validation errors. To ensure smooth upgrades for existing configs:
- When adding a new **required** property, include a `"default"` value in the schema so
  the fix resolver can insert it automatically.
- When adding an **enum** property, always use `"type": "string"` with named values (not
  integers) so the resolver can coerce stale numeric values.
- When **removing** a property, no special action is needed — the resolver detects and
  removes properties disallowed by `additionalProperties: false`.

## Release Channel Rule

Releases are cut by pushing a git tag (full procedure: `CONTRIBUTING.md` → Releasing).
Two conventions the release version gate enforces — violate them and the release fails:

- The tag MUST match `Directory.Build.props`: `<VersionPrefix>` for a stable tag,
  `<VersionPrefix>` + `<VersionSuffix>` for a prerelease.
- Prerelease tags MUST use the **dotted** `beta.N` form (`0.23.0-beta.1`, never `beta1`).
  A mixed identifier like `beta1` is one opaque token that SemVer orders lexically, so
  `beta10` would rank below `beta2` — in the C# comparator *and* the manifest generator.

A tag with a `-` is a prerelease: it becomes a GitHub prerelease and only advances the
Docker `:beta` tag and the manifest `latestPrerelease`. A stable tag moves `:latest`,
`:major.minor`, and the manifest `latest`. The C# `SemVer` comparator and the bash
generator's `feeds/scripts/semver_key.py` are two implementations of one precedence rule
kept in lockstep by a shared fixture (`feeds/scripts/semver-order.txt`) — change both
(and the fixture) together if precedence ever changes. Never hand-edit the release
manifest or installer feed.

## Universal Quality Bar

- secure-by-default behavior for gateway and tools
- no hidden bypasses around ACL/policy checks
- no north-star/deferred features in MVP without explicit PRD update
- actor boundaries remain transport-agnostic (pub/sub over direct transport asks)
- persistence types remain framework-owned and serialization-safe
- **No silent fallbacks.** When something fails or is misconfigured, fail
  loudly — do not silently degrade to a default. Fallbacks hide bugs and
  misconfigurations, and on security-relevant paths they can silently escalate
  privileges. A fallback is only justified when partial failure is a normal
  runtime condition (e.g., a network retry). If you think you need one, ask
  first.
- no new Slopwatch violations: run `/dotnet-skills:slopwatch` after code changes
- use `TimeProvider` (not `DateTime.UtcNow` / `DateTimeOffset.UtcNow`) so time
  can be virtualized in tests. Inject `TimeProvider` via DI; default to
  `TimeProvider.System` in production. Standardize on `DateTimeOffset`, not
  `DateTime`. Usage: `_timeProvider.GetUtcNow()` returns `DateTimeOffset`,
  `.ToUnixTimeMilliseconds()` for persistence timestamps.
- **Reuse before you add — look for existing constructs before creating new
  ones.** Before introducing a new config knob, constructor parameter, field,
  interface, or helper, check what already carries the data you need — especially
  what is *already flowing through the seam you're editing* (the execution
  context, an injected policy, an existing `*Paths`/`*Config` type). A new
  construct that parallels an existing one is a defect: it duplicates state,
  drifts from the original, and forces plumbing (threading a value through N
  constructors) that the existing path already solved. Anchor the design on the
  data already present at the call site; if you find yourself adding plumbing to
  feed a new construct, stop — the value is usually already reachable. Adding a
  new construct is justified only when no existing one fits and you can name why.
- **NEVER add implicit conversions to/from primitive types on value objects.**
  Value objects exist to prevent accidental misuse — an implicit conversion back
  to the primitive defeats the purpose. Use `.Value` for explicit access and
  explicit casts where truly needed. If a value object can silently become a
  string, it provides no more safety than a raw string.
- **Never use the `global::` namespace qualifier in C# source.** Resolve name
  collisions with an ordinary `using` directive or type alias instead.
- **Optional/nullable parameters are rare by default — make dependencies
  required.** A constructor or method parameter should be optional (nullable or
  defaulted) only when its absence is a genuine, intended runtime state the
  callee handles explicitly (a real feature toggle: "no search backend
  configured" → no web-search tool). NEVER add an optional parameter for
  backward/source compatibility or to avoid updating call sites — update the
  call sites instead. This is acute for security-relevant dependencies (path
  roots, deny-list / access / command policies): a nullable security dependency
  consumed with `?.` (`_policy?.IsDenied(p) == true`, `if (_policy is not null)`)
  means a null silently **disables the check** — exactly how trust-zone and
  privilege-escalation bugs hide. Require it so the type system guarantees it is
  always present; if it is unconditionally constructed in production, there is no
  justification for letting it be null.
- **Comments: skip noise, keep signal.** Don't narrate what code does when
  identifiers already say it (`// increment counter`). Do write comments
  that help a human reviewer scanning in isolation: security gate
  explanations (what's blocked, for which audience, why), hidden
  constraints, subtle invariants, non-obvious fallback strategies, and
  cross-cutting concerns that aren't visible from the call site. A good
  comment answers "why would this surprise me?" — not "what does this
  line do?"
- **File organization: do NOT enforce "one type per file."** This repo
  groups closely related types in the same file when that keeps the call
  chain readable — an interface plus its implementations
  (`IToolApprovalMatcher` + `ShellApprovalMatcher` + `DefaultApprovalMatcher`),
  a base type plus its concrete subtypes, a record plus the helpers that
  produce it. Splitting types into separate files purely for the sake of
  separation creates review noise without improving navigation. Reviewers
  (and agents running cleanup passes) SHALL NOT suggest file splits on
  the basis of "one type per file" alone. Real reasons to split: a file
  has grown past ~600 lines AND the contained types serve distinct
  callers; a type genuinely belongs in a different namespace or
  assembly; circular-include relief in generated code.

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
- **TUI / Termina changes MUST be validated with the native smoke
  harness** before being marked done. xUnit cannot drive Spectre-style
  prompts, and the non-interactive smoke scenarios only cover the
  HTTP/JSON surface, so a Termina change that breaks the wizard, model
  picker, provider picker, or any other prompt flow will pass every
  other gate. Run:

  ```bash
  ./scripts/smoke/run-smoke.sh light          # all PR-gating tapes + scenarios
  ./scripts/smoke/run-smoke.sh init-wizard    # one tape, fastest loop
  ```

  See `TOOLING.md` § Interactive CLI Smoke Tests for the full set of
  commands and `tests/smoke/tapes/README.md` for tape authoring
  conventions (no `Sleep`, anchor every step, pair with an assertion).

## Post-Code Quality Check

After any code changes, run:

```bash
dotnet slopwatch analyze                   # Detect reward hacking (new violations fail CI)
./scripts/Add-FileHeaders.ps1 -Verify      # Ensure all .cs files have copyright headers
```

Slopwatch detects: disabled/skipped tests, suppressed warnings, empty catch
blocks, hardcoded values, TODO-as-done comments. Baseline is in
`.slopwatch/baseline.json` — existing entries are accepted, new violations
must be fixed or explicitly baselined with justification.

## Eval Suite

Run the behavioral eval suite (`./evals/run-evals.sh`) when changing:

- Identity file templates (`SOUL.md`, `AGENTS.md`, `TOOLING.md` in init wizard)
- System prompt assembly (`SystemPromptAssembler`, `FileSystemPromptProvider`)
- Skill content (any `SKILL.md` under `feeds/skills/.system/files/`)
- Skill matching logic (`SkillRegistry`, `SystemSkillSyncService` keyword handling)
- Memory pipeline (`SQLiteMemoryRecallCoordinator`, `MemoryProposalGate`,
  checkpoint triggers)
- Compaction logic (`ObservationPromptBuilder`, `ExtractiveSessionReducer`,
  compaction behavior)
- Tool definitions (new tools, changed tool schemas, grant categories)
- Model/provider changes (switching models, changing context window config)
- `SessionConfig` defaults

Update eval cases when:

- Adding a new system skill — add a skill auto-load case
- Adding a new tool — add a tool discovery/use case
- Changing identity grounding rules — update identity assertion patterns
- A production session exhibits a new failure pattern — add a regression case

**Debugging eval failures:** Eval failures are VERY RARELY the model's fault.
Almost always the root cause is an instrumentation issue — how we're parsing or
asserting on the model's output (regex mismatch, output format change, assertion
too brittle). If instrumentation checks out, the next most likely cause is a
genuine alignment problem (system prompt, skill content, or context assembly not
giving the model the right information). Only after ruling out both should you
consider model capability as the cause.

## System Skills Sync Rule

Runtime skill use is logical: call `skill_load` by canonical name and
`skill_read_resource` for bundled files. Do not expose physical skill roots in
prompt indexes or teach agents to derive `SKILL.md` paths. Direct filesystem
inspection is reserved for explicit operator diagnostics.

System skills in `feeds/skills/.system/files/` are the agent's operational
guidance — they tell the running agent how to use features. When you change a
feature area, the corresponding skill **must** be updated in the same PR.

| Feature area changed | Skill to update |
|----------------------|-----------------|
| Identity files, SOUL/AGENTS/TOOLING paths, progressive disclosure | `netclaw-identity` |
| Memory provider routing, SQLite memory tools, general memory guidance | `netclaw-memory` |
| Config format, daemon health, logs, MCP wiring, diagnostics CLI, doctor | `netclaw-operations` |
| Skill file format, discovery, authoring workflow | `skill-authoring` |
| Tool definitions, CLI commands, grant categories, search_tools, scheduling tools | `netclaw-operations` |
| Search tool behavior, citation policy, web_search/web_fetch usage guidance | `search-citation` |
| Workspaces, project creation/discovery | `netclaw-projects` |

**Workflow:**
1. Edit the skill at `feeds/skills/.system/files/{name}/SKILL.md`
2. Bump `metadata.version` in the YAML frontmatter
3. Do NOT run `generate-skill-manifest.sh` locally — CI generates the manifest
   and publishes to R2 on release tags or manual `workflow_dispatch`

**Publishing:**
- Skills publish automatically as part of the binary release workflow (on git tags)
- For hot-patches without a daemon release: `gh workflow run publish_skills.yml`
- Normal dev pushes do NOT publish to the live feed

If a new feature area needs agent guidance, create a new skill file and add a
row to this table.

## Definition of Done

Done means all of the following are true:

- behavior aligns with PRD + spec
- acceptance criteria are testable and verified
- `dotnet slopwatch analyze` passes (no new violations)
- copyright headers present on all `.cs` files (`./scripts/Add-FileHeaders.ps1 -Verify`)
- operational impact is documented (runbooks or CLI help)
- OpenSpec artifacts are updated or archived appropriately
- system skills updated if a mapped feature area was changed (see table above)
- eval suite passes for changes to identity, skills, memory, or tools (see
  Eval Suite section)
- interactive tape harness passes for changes to Termina TUI surfaces
  (init wizard, model/provider/webhook pickers, chat page) — see
  Testing Guidelines and `TOOLING.md` § Interactive CLI Smoke Tests

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
- Testing: akka-testing-patterns, snapshot-testing, testing-strategy
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
- **Compressing a skill into a rule is a retrieval operation, not a
  memory operation.** When distilling a dotnet-skills skill (or any
  upstream authority) into an audit rule, review rubric, or one-line
  bullet, open the skill first. Preserve the distinctions the skill
  draws — not just its headline. Rules that collapse two orthogonal
  axes into one sentence produce false positives that burn
  trust in the audit. If a rule cannot be written without losing a
  distinction the source makes, drop it rather than eliding.
