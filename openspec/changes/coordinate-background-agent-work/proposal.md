## Why

The parent needs a small reusable guide for substantial tasks that need independent child work and reviewed artifacts.
The approved third PR adds that guide after the background execution contract, without private agent messages.

Source requirements: [PRD-001](../../../docs/prd/PRD-001-netclaw-mvp.md) FR-006, FR-011, FR-013, and the second PR's FR-017.
Also use [PRD-004](../../../docs/prd/PRD-004-cli-onboarding-and-config.md) CLI-001A and [PRD-007](../../../docs/prd/PRD-007-agent-personality-and-local-memory.md) identity/workflow outcomes.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## What Changes

- Add parent-inline `agent-coordination/SKILL.md` without `metadata.subagent`.
- Add four workflow resources: analyze-plan, parallel-research, implement-review, and diagnose-fix-verify.
- Add findings and plan Markdown templates as logical skill resources.
- Route substantial parent work through the skill in the embedded runtime `AGENTS.md` and the new-install playbook scaffold.
- Add one general `task-worker` profile with existing `modelRole: Main` for code and complete artifact tasks.
- Reuse the existing CLI identity resource loader and no-overwrite seed path for that profile.
- Reuse existing analyst, research, and summary profiles when their discovered missions fit.
- Add an explicit operator install/update procedure that preserves all existing profile and playbook edits.
- Require artifact checks, distinct writer ownership, independent review, and actual user delivery.

In scope: the coordination skill, its six resources, one profile, runtime guidance, operator procedure, and targeted proof.
Out of scope: a workflow engine, provider overrides, general workspace infrastructure, agent messages, questions, peers, and live steering.
Large operations/memory skill splits remain separate work.

## Capabilities

### New Capabilities

- `agent-coordination`: Progressive workflow selection, structured handoff, workspace ownership, and parent result review.

### Modified Capabilities

- `netclaw-agent-memory`: Embedded and absent-only playbook guidance encourages appropriate autonomous delegation through logical skill access.
- `netclaw-subagents`: One Main-role default worker is seeded without overwrite; existing operators receive an explicit safe install/update procedure.

## Impact

Add the skill tree under `feeds/skills/.system/files/agent-coordination/` through the existing binary bundle.
Add the canonical worker asset under `src/Netclaw.Cli/Resources/identity/task-worker.profile.md`.
`IdentityStepViewModel` can read that exact asset through `ReadEmbeddedTemplate` and seed `task-worker.md` through the existing helper.
The existing CLI resource wildcard already embeds `Resources/identity/*.md`.
Update `netclaw-operations` with the operator procedure and bump its version. Do not regenerate a legacy manifest locally.
Extend relevant skill/subagent cases and preserve seeded-file loader checks plus native init-wizard smoke.

### Security and operational impact

Workflow text and profile tool metadata create no new authority. Every file, shell, profile, and control action retains ordinary runtime policy.
Child results remain attributed under PR 2's original requester contract. Cancellation uses its five-second framework-only partial-result contract.
Concurrent writers receive separate authorized worktrees or serialize their edits. A workflow cannot grant shared-checkout write permission.
The parent's provider capacity gate remains owned by PR 2. This content change cannot resolve or waive it.
