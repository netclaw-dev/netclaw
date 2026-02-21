# Netclaw PRDs

This directory contains product requirements for Netclaw.

## Active PRDs

- `PRD-001-netclaw-mvp.md`
- `PRD-002-gateway-security-envelope.md`
- `PRD-003-operator-ux-ops-console.md`
- `PRD-004-cli-onboarding-and-config.md`
- `PRD-005-model-provider-strategy.md`
- `PRD-006-mcp-tool-integration.md`

## Traceability Rules

- Every engineering spec in `docs/spec/` must reference at least one PRD ID.
- Every OpenSpec change in `openspec/changes/` must include `Source PRDs`.
- Behavior changes cannot be implemented unless covered by either:
  - an existing PRD requirement, or
  - an explicit PRD update in the same planning cycle.

## Planned OpenSpec Capability Mapping

- Session and persistence -> `openspec/specs/netclaw-session/spec.md`
- Gateway security envelope -> `openspec/specs/netclaw-gateway-security/spec.md`
- ACL and policy -> `openspec/specs/netclaw-acl/spec.md`
- Operator UI -> `openspec/specs/netclaw-operator-ui/spec.md`
- CLI -> `openspec/specs/netclaw-cli/spec.md`
- Onboarding -> `openspec/specs/netclaw-onboarding/spec.md`
- Model providers -> `openspec/specs/netclaw-model-providers/spec.md`
- MCP tools -> `openspec/specs/netclaw-mcp/spec.md`
