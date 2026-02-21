# PRD-001: Netclaw MVP

## Status

- State: Draft for execution
- Owner: Netclaw engineering
- Date: 2026-02-21

## Problem Statement

The operator needs a personal assistant that can reliably act through Slack,
keep conversational context across restarts, and remain secure on a homelab
host without requiring a complex distributed deployment.

## Product Goal

Deliver a minimal but dependable Slack-connected assistant that is actor-driven,
persistence-backed, and safe-by-default.

## MVP Success Criteria

1. Netclaw replies in the same Slack thread where the user interacts.
2. Session context survives process restarts.
3. Long sessions compact context while preserving task continuity.
4. Unauthorized interactions are denied by policy.
5. Operator can configure and validate system behavior without source edits.

## Non-Goals (MVP)

- Multi-process gateway/agent split
- sub-agent orchestration framework
- web management UI implementation (spec + mockups only)
- advanced observability and model capability abstractions
- branch/revert session editing features

## Primary Personas

- `Owner-Operator`: runs Netclaw on homelab hardware and interacts through Slack.
- `Future Maintainer`: extends capabilities and needs stable behavioral specs.

## Functional Requirements

### FR-001 Slack Thread Session Identity

Session entity ID shall be `{channelId}/{threadTs}` and all interactions for
that thread shall route to the same session actor.

### FR-002 Turn Processing and Broadcast

User input shall produce a persisted turn event and a broadcast event consumed
by the Slack adapter for reply delivery.

### FR-003 Persistent Recovery

Session state shall recover from PostgreSQL journal and snapshots after
process restart.

### FR-004 Conversation Compaction

When configured thresholds are exceeded, session history shall compact through
summary reduction and persist a compaction event.

### FR-005 Default-Deny ACL

All inbound interactions and privileged operations shall be denied unless
explicitly allowed by configuration.

### FR-006 System Prompt Contract

A file-based system prompt, including opening/zero clause guidance, shall be
injected consistently into turn context.

### FR-007 Operator Controls

CLI commands and documented UI contracts shall cover onboarding, config
validation, ACL diagnostics, and session inspection workflows.

### FR-008 Slack Socket Mode Transport

Slack integration shall use Slack Socket Mode for inbound and outbound message
event handling during MVP, avoiding required public inbound HTTP endpoints.

### FR-009 MCP Tool Integration

Netclaw shall support MCP server integration in MVP so tool capabilities can be
loaded from a configured server list with policy enforcement.

## Operational Requirements

- single-process host deployment on `pi1`
- no required public inbound HTTP path for base Slack operation
- secure failure mode: invalid policy/config blocks startup
- CI/CD test path does not require live model provider credentials

## Acceptance Tests

1. Allowed user posts in Slack thread -> Netclaw replies in thread.
2. Restart host -> same thread follow-up reflects prior context.
3. Long thread triggers compaction without losing active task objective.
4. Disallowed sender/channel is rejected and logged as policy deny.
5. CLI config validation reports pass before runtime start.

## Risks and Mitigations

- `Risk`: accidental security drift while adding convenience features.
  - `Mitigation`: PRD + OpenSpec traceability and default-deny tests.
- `Risk`: persistence model lock-in to unstable message types.
  - `Mitigation`: framework-owned serializable message envelope only.
- `Risk`: MVP scope creep toward north-star architecture.
  - `Mitigation`: explicit non-goals and change reviews against this PRD.
