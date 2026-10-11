## Context

See [proposal.md](proposal.md) for the problem and source PRDs.
The [canonical plan](../../../.systematize/plans/chat-client-local-fsm/plan.html) owns the design, delivery slices, tasks, and verification procedures.
Its baseline is `netclaw-dev/netclaw dev@2e6bc4f014dc96b606566709df1bd11f90ecf34a`.
Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

## Goals / Non-Goals

The [concrete change](../../../.systematize/plans/chat-client-local-fsm/plan.html#example) defines admission and shows the ordered flow.
The [owner map](../../../.systematize/plans/chat-client-local-fsm/plan.html#design) defines actor-local state and daemon journal ownership.
The client controls transport and presentation. The daemon retains model, tool, policy, and durable state responsibilities.
The plan excludes a client outbox, cluster, remoting, automatic uncertain resend, and zero-turn session deletion.

## Decisions

The [owner and behavior decisions](../../../.systematize/plans/chat-client-local-fsm/plan.html#design) use one local actor for both current lifecycle mechanisms.
Identity redo captures the daemon generation before its config write.
It reuses the wizard readiness component before the explicit guided chat handoff.
A prior running daemon must report a newer generation; a prior stopped daemon must pass its start and health check.
A running daemon without a generation blocks the identity write.
The event pump retains subscriber isolation. The Termina loop retains UI mutation authority.
`IWithTimers` controls retry, RPC timeout, and the accepted two-second close deadline.
Tests advance `Akka.TestKit.TestScheduler`; `TimeProvider` remains the source for UTC timestamps.
The actor permits one transport operation at a time. A stale result cannot alter current state.
Cancellation does not prove that transport side effects stopped.
The daemon text path reuses `AckTarget` and waits for its journal acknowledgment.
The shared acknowledgment type retains its meanings outside text ingress.
The [smaller alternative](../../../.systematize/plans/chat-client-local-fsm/plan.html#evidence) retains the eager client with an admission correction and explicit errors.
That alternative retains lifecycle overlap and empty sessions from open-and-quit.

## Risks / Trade-offs

- Old daemons return before admission. The explicit compatibility gate must reject unsupported daemon behavior.
- The prior report describes loss after a state transition. Real disconnect proof must establish that boundary before client cutover.
- Actor probes cannot prove journal or terminal behavior. The plan retains real boundary tests and native tapes.
- Close can end with unresolved delivery. Its receipt separates an active uncertain RPC from queued unsent text.
- A new actor adds code. The review must measure net change without weaker coverage.

See [V1–V8](../../../.systematize/plans/chat-client-local-fsm/plan.html#verification) for repeatable proof and negative controls.

## Migration Plan

The [Delivery section](../../../.systematize/plans/chat-client-local-fsm/plan.html#delivery) defines two PRs and their dependency.
The daemon contract precedes the client cutover. Both old client lifecycle paths disappear in the same cutover PR.
The plan defines compatibility, intermediate behavior, rollback limits, and retirement steps.
Product implementation, publication, merge, and deployment remain separate from this plan.
