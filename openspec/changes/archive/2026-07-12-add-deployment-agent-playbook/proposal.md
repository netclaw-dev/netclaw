## Why

Operators can customize Netclaw's personality and environment, but cannot define a durable deployment mission and operating playbook that applies to both the main agent and delegated sub-agents. This leaves role-specific workflows—such as selecting sales-writing skills and reviewing customer-facing email—dependent on ad hoc prompting and causes inconsistent output quality.

Source PRDs: PRD-004, PRD-007.

## What Changes

- Load `~/.netclaw/identity/AGENTS.md` as the operator-authored deployment mission and operating playbook after Netclaw's embedded operating rules.
- Apply the same deployment playbook to Personal, Team, and Public sessions and inherit it into sub-agents through the existing operating-rules seam.
- Seed a minimal playbook scaffold on fresh initialization without overwriting an existing file.
- Extend the post-init conversation to discover, confirm, and persist the deployment mission separately from operator and personality information.
- Replace the obsolete eval identity template with a purpose-built mission fixture and add main-agent and sub-agent adherence coverage.
- Add operational guidance for authoring and safely maintaining deployment identity files.

In scope for MVP: prompt composition, initialization, post-init guidance, sub-agent inheritance, documentation, deterministic tests, and behavioral evals. Out of scope: audience-specific AGENTS variants, configuration knobs, hard security enforcement through prompt text, and automatic migration or rewriting of existing customized files.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `netclaw-agent-memory`: Define the disk AGENTS layer as the deployment mission/playbook, its initialization behavior, audience behavior, and live refresh semantics.
- `netclaw-subagents`: Require sub-agents to inherit the audience-appropriate embedded operating core plus the deployment playbook.
- `netclaw-onboarding`: Extend post-init conversational onboarding to author the mission playbook while preserving existing files.

## Impact

- Prompt assembly in `Netclaw.Configuration` and sub-agent prompt documentation/protocol comments.
- CLI identity templates, initialization finalization, and the post-init chat trigger.
- Identity system-skill guidance and operator documentation.
- Configuration, CLI, actor, smoke, and behavioral eval coverage.
- No new configuration schema property or external dependency.

Security impact: the playbook is trusted operator-authored prompt guidance, not a security boundary. Embedded rules remain higher-priority by contract, while ACL and tool-policy enforcement remain authoritative. Because one playbook is supplied to every configured audience, documentation prohibits secrets and audience-private data in this file.

Operational impact: edits are read before each inbound turn and therefore affect the next turn without daemon restart. Missing playbooks remain an intentional supported state; unexpected read failures are surfaced rather than replaced with alternate content.
