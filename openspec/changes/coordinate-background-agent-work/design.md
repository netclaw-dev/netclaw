## Context

See [proposal.md](proposal.md) for the product need and PRD traceability.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.
The source baseline is `2e6bc4f014dc96b606566709df1bd11f90ecf34a`.
The approved [plan](../../../../plan-background-subagents/.systematize/plans/background-subagents/plan.html) defines this third PR.
The plan file lives in the sibling `plan-background-subagents` worktree until the stack includes its canonical documents.
PR 1 owns recurrence policy. PR 2 owns child lifetime, control, authority, cancellation, and result delivery.
This PR adds instructions and one default profile. It adds no actor protocol or persistence schema.

Current source seams:

| Concern | Current owner and evidence | Data lifetime |
|---|---|---|
| Platform instructions | `src/Netclaw.Configuration/Resources/AGENTS.md`, platform core in prompt assembly | Embedded release asset |
| Deployment playbook | CLI `Resources/identity/AGENTS.template.md`; absent-only creation | Operator-owned file |
| Profile defaults | `IdentityStepViewModel.SeedBuiltInAgents`, `SeedAgentFile` | Operator-owned files after first seed |
| Profile resource | `ReadEmbeddedTemplate`, CLI `Resources/identity/*.md` wildcard | Embedded release asset |
| Model role | Loader parses `modelRole`; spawner passes it to `GetClient` | Loaded profile and child run |
| Skill resources | Daemon embeds `feeds/skills/.system/files/**`; logical skill APIs enforce access | Release bundle and call-local result |
| Child controls | PR 2 deferred tool and durable run ledger | Owner-session durable state |

## Goals / Non-Goals

**Goals:**

- Give the parent a short workflow selector and precise resource handoffs.
- Encourage delegation for substantial code, analysis, research, and complete artifact tasks.
- Keep the parent responsible for scope, authority, evidence review, and user delivery.
- Preserve operator files and use existing profile and model-role seams.

**Non-Goals:**

- No workflow engine, shared-worktree write grant, profile installer, or provider configuration.
- No child questions, private messages, peer discovery, or live steering.
- No changes to specialist missions or the global default `ModelRole.Compaction`.
- No task lifetime ceiling or automatic child relaunch.

## Decisions

### 1. Select one workflow through logical skill resources

Add `feeds/skills/.system/files/agent-coordination/SKILL.md` with `metadata.version: 1.0.0`.
Omit `metadata.subagent`. The parent must select and direct children itself.
The skill remains model-invocable under existing discovery, audience, and access rules.
Its description covers substantial code tasks, architecture plans, independent research, and defect analysis.
The base text includes authority, workspace ownership, artifact review, cancellation, and delivery rules.
Read only the selected workflow with `skill_read_resource`; do not load all workflows by default.

Bundle these exact logical resources:

| Resource | Ordered contract |
|---|---|
| `references/analyze-plan.md` | Analysis child records evidence; parent checks it; worker writes a complete Markdown plan; parent checks traceability and delivers it. |
| `references/parallel-research.md` | Parent assigns distinct questions and result paths; children cite evidence; parent resolves conflicts and reports uncertainty. |
| `references/implement-review.md` | Worker edits an assigned workspace; worker stops edits; separate reviewer inspects the exact revision; parent validates and integrates. |
| `references/diagnose-fix-verify.md` | Analyst records the defect; worker repairs it; separate verifier tests the counterexample; parent compares baseline and candidate evidence. |
| `assets/findings.md` | Template for evidence IDs, source revisions, component owners, constraints, findings, uncertainty, and unresolved choices. |
| `assets/plan.md` | Template for the problem, source findings, scope, actions, acceptance checks, risks, and open decisions. |

The templates contain placeholders. The parent must not present a template as a completed artifact.
The base selector fits below the effective inline result limit with its wrapper and resource list.
Each selected resource fits below the default 12,000-character inline limit with its resolved-path header.
Tests measure actual returned payloads, rather than source line counts.
Smaller operator limits retain normal spill behavior and `tool_output_read`; instructions must not teach physical skill-root access.

Alternative: one large coordination skill. This increases default context and hides the selected workflow inside unrelated instructions.
Alternative: route the skill directly to a child. This removes the parent from the coordination and delivery decisions.

### 2. Use explicit child tasks and artifact handoffs

Every child task states the question, assigned revision, allowed workspace, output path, acceptance checks, and forbidden edits.
The parent discovers available profiles before it selects a mission.
Use `research-assistant` for external evidence, `code-analyst` for source analysis or review, and `summarizer` for concise summaries.
Use `task-worker` for implementation or a complete plan artifact.
The parent does not assume that a summary profile can produce a complete plan.
Use a child only when the independent task has useful scope. A trivial local edit needs no child.

The parent inspects the returned artifact and its revision before it accepts the result.
An artifact path, model claim, success flag, or reviewer agreement does not prove task completion.
The reviewer receives the exact candidate revision, the original acceptance checks, and the worker's evidence.
The reviewer remains distinct from the worker and does not edit the candidate patch.
If a review command writes build output, assign an authorized isolated workspace for that command.
The parent resolves disagreements from evidence and records remaining uncertainty.

The parent delivers requested Markdown artifacts through the existing attachment tool and normal policy.
A file on disk does not prove user delivery. Report a blocked attachment as a delivery limitation.
Children return paths and results; they do not attach files to the user.

### 3. Assign one writer to each workspace

Read-only children can share an authorized source revision. Assign distinct artifact paths for their reports.
Concurrent writers require separate authorized worktrees and distinct task scope.
Use existing git, shell, and file tools to prepare worktrees. This skill creates no workspace service or permission.
Preserve a dirty operator checkout. Do not reset, stash, or discard its changes to create a clean task.
If isolation is unavailable, serialize edits within the permitted workspace and state the constraint.
The parent must not interpret disjoint filenames as permission for concurrent writes in one checkout.
The parent verifies the patch before it integrates a child branch.

Positive: two read-only analysts inspect one revision and write separate reports in assigned result directories.
Negative: two workers change different files in one dirty checkout without explicit shared-write authorization.

### 4. Reuse background control and original authority

The ordered flow below is schematic. Normal admission, policy, persistence, and provider gates still apply.

```text
parent identifies useful independent work
  -> skill_load("agent-coordination")
  -> skill_read_resource(selected logical resource)
  -> prepare authorized task scope and artifact paths
  -> spawn_agent(discovered profile, explicit task)
  -> receive accepted run_id
  -> parent continues independent work
  -> attributed terminal result arrives under PR 2
  -> parent reads evidence, checks revision, reviews and delivers
```

The parent uses `load_tool` to load `check_agent_run` only when status or cancellation is necessary.
The skill must not add that deferred schema to the initial tool set.
Routine progress checks do not create a fast poll loop. Normal completion uses the attributed terminal result.
Only the owner session and original eligible requester can use status/cancel under PR 2's current policy gates.
An accepted cancellation does not prove `dispatch_closed=true` or terminal completion.
The parent waits for the explicit closure/terminal evidence before it treats the child as stopped.
Cancellation preserves a bounded partial result through the five-second framework-only finalization path.
The parent inspects known effects and unknown effects before it creates a replacement child.
Replacement needs a revised task or a justified next attempt; cancellation does not undo prior side effects.
The skill offers no live steering, private messages, child questions, or peer controls.
Lost means explicit loss after restart. The parent does not claim automatic resume.

Positive: the original requester cancels an owned run and receives a partial result with unknown external effects.
Negative: a workflow tells a different speaker to control the run or gives a child an unavailable message tool.

### 5. Seed one canonical Main-role worker asset

Add `src/Netclaw.Cli/Resources/identity/task-worker.profile.md` as the canonical release asset.
The existing wildcard embeds this exact filename without a new project resource rule.
`ReadEmbeddedTemplate("task-worker.profile.md")` resolves `Netclaw.Cli.Resources.identity.task-worker.profile.md`.
`SeedBuiltInAgents` passes that content to the existing `SeedAgentFile` helper with destination `task-worker.md`.
Keep the three existing specialist profiles unchanged.

The new profile has these fields:

```yaml
name: task-worker
description: Execute a scoped code task or produce a complete artifact
modelRole: Main
timeoutSeconds: 120
visibility: user-facing
```

Omit a per-profile provider override, tool grant, and prefill timeout override.
The existing timeout is an inactivity bound, not a lifetime budget.
The worker body requires assigned scope, full artifacts, evidence, effect uncertainty, and an honest terminal result.
It does not copy the platform constitution or add recursive delegation rules.

The loader already accepts `Main`; the spawner already passes the profile role to the chat-client provider.
`Main` uses configured primary-role behavior, including existing permitted fallback behavior.
It does not guarantee a particular provider or model ID.
Code execution and complete artifacts justify the Main role. Existing cheaper specialist roles remain available for their missions.
An operator-owned `task-worker.md` remains authoritative even when it differs from the default.
The parent discovers its current mission and does not force the release default onto it.

Alternative: use the code analyst for all execution. Its existing mission emphasizes analysis and review.
Alternative: change the global default role. This changes every operator profile that omits a role.

### 6. Preserve operator files and short runtime guidance

Update the embedded runtime `AGENTS.md` delegation section with a short parent-specific route.
Add the same short route to the absent-only CLI playbook scaffold under Delegation.
The route names useful code work and the logical coordination skill. It does not contain four full workflow descriptions.
Start the route with parent-specific language because children inherit the same deployment playbook.
Keep child spawn prohibition, attachment ownership, ordinary authorization, and the existing layered prompt contracts.
An upgrade must not rewrite an existing deployment playbook.

Add an operator install/update procedure to `netclaw-operations` and `docs/runbooks/subagents.md`.
Bump the operations skill version. Add the new feature row to the system-skill sync map when implementation changes the skill.
The procedure uses the canonical `task-worker.profile.md` asset from the exact release source.
It identifies the configured agent directory through existing configuration and operator diagnostics.
For an absent destination, the operator reviews and copies the asset to `task-worker.md` under normal file policy.
For an existing regular file, the operator compares the asset and preserves the current file unless they explicitly approve an update.
For a directory, symbolic link, or unresolved destination, the procedure stops and reports the conflict.
Do not invent an installer command or recommend a repeat init wizard as a profile update operation.
Physical agent-file diagnostics belong to operators. Skill bundle access remains logical for runtime agents.
The existing profile seed helper remains the absent-only default path; no broad filesystem rewrite belongs in this PR.

## Risks / Trade-offs

- More autonomous delegation adds cost and contention. Use useful independent scopes and inspect actual results.
- A short selector can lose essential rules. Keep common boundaries in the base and test actual returned payloads.
- Child success can hide stale evidence. Bind handoffs and independent review to the exact revision.
- Separate worktrees add setup cost. Reuse existing tools and serialize writes when isolation is unavailable.
- Main-role work can use more resources. Preserve configured role behavior and avoid provider overrides.
- A repeat init can replace unrelated identity content. Use the explicit asset procedure for existing deployments.
- Restart can lose an active child. Inherit PR 2's Lost result and preserve confirmed partial evidence.
- A child can consume a single provider slot. PR 2's real-model responsiveness gate remains unpassed until capacity is resolved.

### Why this is safe

This PR adds no authority owner, grant scope, actor messages, or persistence events.
The runtime still validates every child, tool, file, and model-role selection through its existing gates.
The seed path preserves existing operator profiles and playbooks.
Distinct workspaces and independent evidence review reduce damage from a wrong child result.
The proof plan separates scripted protocol evidence from real-model workflow evidence.
None of these claims proves arbitrary model judgment or productive work for days.

## Migration Plan

1. Apply PR 1 and PR 2 before this contract. Validate this PR on the combined stack.
2. Ship the coordination bundle and the worker asset through the existing binary resource paths.
3. Seed the worker only during the existing new-install path when its destination is absent.
4. Give existing operators the explicit reviewed install/update procedure.
5. Run targeted skill/model cases, seeded-file loader checks, and native init-wizard smoke before acceptance.
6. Before rollback, follow the [subagent rollback runbook](../../../docs/runbooks/subagents.md#exact-recurrence-upgrade-and-rollback).
   The inherited persistence contract requires a tested compatible reader or restoration of a recorded pre-upgrade backup.
   Only then use the prior binary and bundle. Keep all operator-owned profiles and playbooks.
   Backup restoration loses post-backup journal state and does not undo external effects. This plan does not prove an executed rollback.

Independent asset and default-profile preparation can precede stack integration.
Every dependent runtime and model gate still requires the combined first and second PRs.
No live feed publication, daemon upgrade, or operator file replacement follows from independent preparation.
The release task must state how the binary bundle and any separately maintained skill feed reach consistent versions.
Do not run the legacy manifest generator locally.

## Open Questions

The provider capacity disposition remains a PR 2 owner decision.
This PR neither narrows the parent response guarantee nor adds a scheduler.
The exact final prose of each workflow remains subject to artifact and behavioral review within this contract.
No other unresolved design choice blocks this contract.

## Independent Preparation Evidence

The initial preparation uses the stated baseline without PR 1 or PR 2 integration.
The seed-to-loader checks passed within 12 identity tests.
The later identity suite passed all 13 tests, with no skips.
Its worker test loads the seeded canonical profile and uses the real `SubAgentSpawner` and `SubAgentActor`.
The provider wrapper records the Main role and delegates to the existing `SingleClientProvider` and scripted `FakeChatClient`.
The test verifies one actual provider request, the canonical worker mission, and the exact task section.
This check proves the role path on the preparation baseline; it does not prove background start, model quality, or artifact completion.
The bundle and logical resource checks passed within 10 daemon seed tests.
All 18 profile-loader tests passed, including the omitted-role default and operator profile priority.
The daemon seed suite skipped its existing Windows-only junction case on Linux.
These checks prove the canonical profile asset, no-overwrite behavior, inline skill load, six resources, and Public denial.
They do not prove the combined background protocol, Main-role inference quality, or days-long productivity.

The targeted `skill_coordination_discovery` case measures discovery and one logical workflow resource receipt.
It measures no workflow execution, child result quality, or complete artifact delivery.
Its Python controls exercise runtime-shaped JSON and timestamped headless log records without model inference.
The fixture home retains its mission and appends the canonical short parent route.
Final integration must recheck route extraction after PR 2 changes the runtime delegation text.
All 10 coordination receipt controls and three native profile-oracle controls passed.
The native init-wizard tape and its external file assertions passed on this independent preparation.
The native oracle compares the worker with the canonical asset and preserves a custom specialist byte for byte.
Its controls reject an absent worker and an altered operator profile, including paths that contain spaces.
Slopwatch found no new issues. Copyright-header verification passed through `pwsh -NoProfile -File` because the script lacks an executable bit.
The source changes add no policy check, grant scope, or authorization owner; the existing focused mutation scope remains unchanged.
Final combined-stack verification and real-model trials remain open.
