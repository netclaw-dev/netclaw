# PRD-003: Operator UX - Ops Console

## Status

- State: Draft for execution
- Owner: Netclaw engineering
- Date: 2026-02-21
- Depends on: `PRD-001`, `PRD-002`

## Goal

Define a management experience that lets an owner-operator understand system
health, inspect sessions, and control policy without reading source code.

## UX Direction

Ops console, not chat-first dashboard:

- dense information layout
- fast diagnostics and actionability
- explicit security and policy visibility

## Jobs To Be Done

1. As an operator, I can see if Netclaw is healthy and connected.
2. As an operator, I can inspect any Slack-thread session quickly.
3. As an operator, I can review and update ACL rules safely.
4. As an operator, I can verify exposure mode and security posture at a glance.
5. As an operator, I can diagnose failures from one place.

## Core Screens

### UX-001 Overview

Surface runtime health, active sessions, policy deny counters, and recent
critical events.

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

Provide filtered logs, actor errors, persistence status, and operator guidance
for next actions.

### UX-006 UI Stack (Delivery)

The management console implementation target is ASP.NET Core with Blazor Server
components in the same host process for MVP. Real-time updates use SignalR.

Rationale: keeps deployment and security surface simple, avoids separate Node
build/runtime, and aligns with .NET-first operations.

## UX Requirements

- all security states use explicit labels (never implicit)
- dangerous actions require confirmation and rationale text
- validation errors are actionable and location-specific
- mobile layout supports read-only triage and acknowledge actions

## Out of Scope (MVP)

- full web UI implementation
- visual analytics historical dashboards
- multi-tenant role-based admin views

## Acceptance Criteria

1. Mockups define all core screens with component-level content.
2. UI spec defines data contracts needed from runtime.
3. Every operator workflow has an equivalent CLI path for MVP.
