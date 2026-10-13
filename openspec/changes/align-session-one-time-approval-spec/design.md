## Context

The `netclaw-session` requirement assigns a session grant to `ApproveOnce`. It also assigns approval channel ownership and wait completion to the actor. The [tool authorization spec](../../specs/tool-authorization/spec.md) and [session contract test plan](../../../docs/research/session-contract-test-plan.md) use exact-call consent. `SessionToolWorkflow` owns the channel and returns typed approval outcomes. The actor journals accepted parent answers before the workflow releases live calls.

## Goals / Non-Goals

**Goal:** Align the session requirement with the current consent contract and workflow ownership.

**Non-Goals:** Change actor code, journal events, grant storage, prompt options, or child consent.

## Decisions

Replace the stale grant-scope and channel-ownership text in the complete persisted turn lifecycle requirement. Keep its other text and scenarios. Add two scenarios for one-time consent. Revise the Processing response scenario to show typed acceptance and journal-before-release order. Add a live-only child scenario.

Keep parent approval recovery in TA-11 and child live-only consent in TA-12. A parent answer can survive restart without a reusable grant. A child prompt expires after a restart. The actor owns the journal operation. The workflow owns response validation and live wait completion.

A new approval policy was an alternative. It would change security behavior and contradict current code, tests, and consent rules. This change keeps the current policy.

## Risks / Trade-offs

- A partial `MODIFIED` block could remove old scenarios during archive. Copy the full requirement and retain each existing scenario.
- A reader could confuse a durable answer with a durable grant. State that `ApproveOnce` creates no session or persistent grant.
- A reader could infer that a child answer uses the parent journal. State that child approval waits remain live-only.

## Migration Plan

Sync the delta into `openspec/specs/netclaw-session/spec.md`. No data migration, deployment step, or runtime rollback applies. Revert the spec sync if source evidence later disproves this contract.
