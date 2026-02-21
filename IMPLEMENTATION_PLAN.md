# Netclaw Implementation Plan

Last updated: 2026-02-21
Mode: build

## Planning Baseline Status

- PRDs established under `docs/prd/`
- Engineering specs established under `docs/spec/`
- UI mockups established under `docs/ui/`
- OpenSpec capabilities established under `openspec/specs/`
- Active OpenSpec changes:
  - `define-netclaw-mvp-foundation`
  - `design-ops-console-and-cli-v1`
  - `add-guided-onboarding-and-provider-strategy`
  - `add-mcp-support-v1`
  - `add-provider-smoke-and-ci-independence`

## Milestones

### M0 - Foundation Planning Baseline

Objective:

- finalize planning artifacts and spec traceability before runtime work

Source PRDs:

- `docs/prd/PRD-001-netclaw-mvp.md`
- `docs/prd/PRD-002-gateway-security-envelope.md`

Source Specs:

- `docs/spec/SPEC-001-runtime-boundaries.md`
- `docs/spec/SPEC-002-session-lifecycle-and-protocol.md`
- `docs/spec/SPEC-003-acl-policy-and-security-controls.md`

Source OpenSpec:

- capabilities: `openspec/specs/netclaw-session/spec.md`,
  `openspec/specs/netclaw-gateway-security/spec.md`,
  `openspec/specs/netclaw-acl/spec.md`,
  `openspec/specs/netclaw-slack-socket/spec.md`
- change: `openspec/changes/define-netclaw-mvp-foundation/`

Done when:

- planning artifacts are validated and accepted
- change tasks are marked complete and ready for archive

### M1 - Slack Session Vertical Slice

Objective:

- process Slack thread message -> actor turn -> persisted event -> thread reply

Source PRDs:

- `docs/prd/PRD-001-netclaw-mvp.md`

Source Specs:

- `docs/spec/SPEC-001-runtime-boundaries.md`
- `docs/spec/SPEC-002-session-lifecycle-and-protocol.md`

Source OpenSpec:

- capabilities: `openspec/specs/netclaw-session/spec.md`,
  `openspec/specs/netclaw-slack-socket/spec.md`

Done when:

- allowed sender gets in-thread response
- restart preserves thread session context

### M2 - Security Envelope and ACL Enforcement

Objective:

- enforce default-deny policy, mention/ambient rules, and exposure policy

Source PRDs:

- `docs/prd/PRD-002-gateway-security-envelope.md`

Source Specs:

- `docs/spec/SPEC-003-acl-policy-and-security-controls.md`
- `docs/spec/SPEC-006-gateway-exposure-and-remote-access.md`

Source OpenSpec:

- capabilities: `openspec/specs/netclaw-acl/spec.md`,
  `openspec/specs/netclaw-gateway-security/spec.md`

Done when:

- disallowed interactions are denied with reason codes
- public exposure without access policy fails validation

### M3 - Guided Onboarding, Provider Strategy, and MCP

Objective:

- deliver onboarding-first CLI with OpenRouter default, multi-provider support,
  and MCP integration

Source PRDs:

- `docs/prd/PRD-004-cli-onboarding-and-config.md`
- `docs/prd/PRD-005-model-provider-strategy.md`
- `docs/prd/PRD-006-mcp-tool-integration.md`

Source Specs:

- `docs/spec/SPEC-004-cli-contract.md`
- `docs/spec/SPEC-007-guided-onboarding.md`
- `docs/spec/SPEC-008-model-provider-abstraction.md`
- `docs/spec/SPEC-009-mcp-integration.md`
- `docs/spec/SPEC-010-testing-and-smoke-strategy.md`

Source OpenSpec:

- capabilities: `openspec/specs/netclaw-cli/spec.md`,
  `openspec/specs/netclaw-onboarding/spec.md`,
  `openspec/specs/netclaw-model-providers/spec.md`,
  `openspec/specs/netclaw-mcp/spec.md`,
  `openspec/specs/netclaw-testing/spec.md`
- change: `openspec/changes/add-guided-onboarding-and-provider-strategy/`
- change: `openspec/changes/add-mcp-support-v1/`
- change: `openspec/changes/add-provider-smoke-and-ci-independence/`

Done when:

- `netclaw init` completes guided setup from blank config
- provider validation reports clear remediation
- MCP server validation and diagnostics report clear remediation
- optional Ollama smoke checks run locally when invoked
- required CI test path passes without live provider credentials

### M4 - Ops Console and Diagnostics Surface

Objective:

- implement ops-console routes and runtime diagnostics contracts

Source PRDs:

- `docs/prd/PRD-003-operator-ux-ops-console.md`

Source Specs:

- `docs/spec/SPEC-005-operator-ui-contract.md`
- `docs/spec/SPEC-004-cli-contract.md`

Source OpenSpec:

- capabilities: `openspec/specs/netclaw-operator-ui/spec.md`,
  `openspec/specs/netclaw-cli/spec.md`
- change: `openspec/changes/design-ops-console-and-cli-v1/`

Done when:

- operator can inspect health, sessions, policy, security, and diagnostics
- every critical UI flow has CLI parity

### M5 - pi1 Acceptance

Objective:

- verify full MVP behavior on target host (`pi1`)

Source PRDs:

- `docs/prd/PRD-001-netclaw-mvp.md`

Source Specs:

- all specs required by M1-M4

Done when:

- Slack in-thread responses work reliably
- recovery works across restart
- compaction preserves context
- ACL and exposure controls enforce policy as specified

## Execution Notes

- implement through OpenSpec changes and archive when verified
- update this file whenever milestone scope or source specs change
