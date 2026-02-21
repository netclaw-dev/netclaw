# SPEC-001: Runtime Boundaries

Source PRDs: `PRD-001`, `PRD-002`

## Purpose

Define the logical boundaries between gateway adapters, session actors, and
persistence in single-process MVP deployment.

## Boundary Model

1. `Gateway boundary`: receives transport events, performs policy checks, routes
   allowed interactions to actor commands.
2. `Session boundary`: owns conversation state, turn lifecycle, compaction,
   persistence events, and broadcasts.
3. `Subscriber boundary`: Slack adapter (and future UI) consumes broadcasts,
   never reads actor internals directly.

## Contracts

- Gateway -> Session: command messages only.
- Session -> Gateway/UI: broadcast messages only.
- Persistence: session actor stores framework-owned serializable events and
  snapshots.
- Primary transport in MVP: Slack Socket Mode adapter.

## Security Requirements

- policy checks must complete before command dispatch
- no bypass path from gateway input directly to tool execution
- startup rejects invalid policy config

## Failure Behavior

- deny-by-default on policy evaluation errors
- recover session actor state from persistence before processing new turns
- keep adapter retries outside actor state machine where possible
