# Netclaw PRDs

This directory contains product requirements for Netclaw.

## Active PRDs

| PRD | Title | Phase | Status |
|-----|-------|-------|--------|
| `PRD-001-netclaw-mvp.md` | Netclaw MVP | 1 | Revised |
| `PRD-002-gateway-security-envelope.md` | Gateway Security Envelope | 1 | Revised |
| `PRD-003-operator-ux-ops-console.md` | Operator UX - Ops Console | 5 (deferred) | Revised |
| `PRD-004-cli-onboarding-and-config.md` | CLI Onboarding and Configuration | 1-2 | Revised |
| `PRD-005-model-provider-strategy.md` | Model Provider Strategy | 1 | Revised |
| `PRD-006-mcp-tool-integration.md` | MCP Tool Integration | 1 | Revised |
| `PRD-007-agent-personality-and-local-memory.md` | Agent Personality and Local Memory | 1 | New |
| `PRD-008-scheduling-and-periodic-tasks.md` | Scheduling and Periodic Tasks | 1 | New |
| `PRD-009-input-adapters-and-unified-input.md` | Input Adapters and Unified Input | 1 (partial), 2+ | New |

## Revision History

- **2026-02-21 (initial)**: PRDs 001-006 created for narrow "chat assistant
  with ACL" scope.
- **2026-02-21 (expanded)**: All PRDs revised to match expanded product vision.
  Netclaw is now an always-on autonomous operations agent. PRDs 007-009 added
  for local memory, scheduling, and input adapters.

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
- Agent personality and memory -> `openspec/specs/netclaw-agent-memory/spec.md` (new)
- Scheduling -> `openspec/specs/netclaw-scheduling/spec.md` (new)
- Input adapters -> `openspec/specs/netclaw-input-adapters/spec.md` (new)
- Config hot-reload -> `openspec/specs/netclaw-config-hot-reload/spec.md` (new)
- First-party tools -> `openspec/specs/netclaw-tools/spec.md` (new)

## Cross-Reference Matrix

| PRD | Depends On | Depended By |
|-----|-----------|-------------|
| 001 | — | all others |
| 002 | 001 | 003, 004, 006, 007, 008, 009 |
| 003 | 001, 002 | — |
| 004 | 001, 002 | — |
| 005 | 001, 004 | — |
| 006 | 001, 002, 004 | — |
| 007 | 001, 002 | 008 |
| 008 | 001, 002, 007 | — |
| 009 | 001, 002, 008 | — |
