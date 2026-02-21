# PRD-003: Operator UX - Ops Console

## Status

- State: Deferred to Phase 5
- Owner: Netclaw engineering
- Date: 2026-02-21
- Revised: 2026-02-21 (explicitly deferred; spec/mockup deliverable clarified)
- Depends on: `PRD-001`, `PRD-002`

## Deferral Note

The ops console web UI is **Phase 5** work. During MVP (Phase 1), all operator
workflows are served by CLI commands (PRD-004). This PRD defines the target
experience so CLI and management API contracts can be designed with the future
UI in mind.

**MVP deliverable:** UI spec and mockups only. No implementation.

## Goal

Define a management experience that lets an owner-operator understand system
health, inspect sessions, control policy, review memory and scheduling state,
and diagnose failures without reading source code.

## UX Direction

Ops console, not chat-first dashboard:

- dense information layout
- fast diagnostics and actionability
- explicit security and policy visibility
- memory and scheduling visibility

## Jobs To Be Done

1. As an operator, I can see if Netclaw is healthy and connected.
2. As an operator, I can inspect any Slack-thread session quickly.
3. As an operator, I can review and update ACL rules safely.
4. As an operator, I can verify exposure mode and security posture at a glance.
5. As an operator, I can diagnose failures from one place.
6. As an operator, I can see registered projects and environment inventory.
7. As an operator, I can see and manage scheduled tasks.
8. As an operator, I can see agent memory and personality configuration.
9. As an operator, I can see MCP server health and tool availability.

## Core Screens

### UX-001 Overview

Surface runtime health, active sessions, policy deny counters, scheduled task
status, memory health, and recent critical events.

### UX-002 Session Inspector

Search session list by channel/thread, inspect last turns, recovery status,
compaction status, and current policy context.

### UX-003 ACL and Policy Editor

Edit policy JSON through a guided editor with validation and effective-policy
preview before apply.

### UX-004 Gateway Security

Display bind mode, exposure mode, pairing/approval status, and high-risk
warnings.

### UX-005 Diagnostics

Provide filtered logs, actor errors, persistence status, tool invocation
audit trail, and operator guidance for next actions.

### UX-006 Memory and Configuration

View agent personality files, project registry, environment inventory, and
allow editing through the UI (same validation as self-configuration).

### UX-007 Scheduling

View, create, pause, and delete scheduled tasks. Show execution history and
next-fire times.

### UX-008 UI Stack (Delivery)

The management console implementation target is ASP.NET Core with Blazor Server
components in the same host process. Real-time updates use SignalR.

Rationale: keeps deployment and security surface simple, avoids separate Node
build/runtime, and aligns with .NET-first operations.

## UX Requirements

- all security states use explicit labels (never implicit)
- dangerous actions require confirmation and rationale text
- validation errors are actionable and location-specific
- mobile layout supports read-only triage and acknowledge actions

## Out of Scope

- visual analytics historical dashboards
- multi-tenant role-based admin views
- implementation during MVP (Phase 1-4)

## Acceptance Criteria (Phase 5)

1. Mockups define all core screens with component-level content.
2. UI spec defines data contracts needed from runtime.
3. Every operator workflow has an equivalent CLI path.
4. Management API endpoints exist for all UI data needs.

## Acceptance Criteria (MVP - spec only)

1. UI screen definitions exist as mockups in `docs/ui/`.
2. Management API contract is documented for future implementation.
3. CLI commands in PRD-004 cover all operator workflows.

## Cross-References

- CLI alternative: PRD-004
- Security controls: PRD-002
- Memory system: PRD-007
- Scheduling: PRD-008
