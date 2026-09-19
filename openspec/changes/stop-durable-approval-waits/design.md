## Context

See [the proposal](proposal.md) for the delay. `LlmSessionActor` keeps the active tool task alive while `SessionToolExecutionPipeline` waits for an approval. A `ToolApprovalRequested` event records a durable parent-session prompt. `ToolCallRecorded` records a completed sibling result. The pipeline now has no explicit stop acknowledgment.

The session actor owns the live task and the drain decision. The session journal owns approval and result state. The actor buffer and deferred approval response are actor-local data. The [engineering glossary](../../../docs/spec/GLOSSARY.md) defines shared terms.

## Goals / Non-Goals

**Goals:**

- Stop a tool task after all unfinished calls have durable approval records.
- Acknowledge drain only after that task stops.
- Keep approved tool work safe for cold recovery under its original turn authority.

**Non-Goals:**

- Resume a canceled model call or accepted input automatically.
- Shorten the global stop limit before other active states have safe paths.

## Decisions

### The actor checks each call at the journal boundary

The active batch tracker knows expected call IDs and applied result IDs. The approval state knows each pending call and whether its prompt is durable. The actor uses both sets after event callbacks apply. It does not trust a prompt that has only entered the actor mailbox.

The actor can use the fast path only when each expected call has an applied result or an unresolved durable approval. At least one call must await approval. The actor excludes accepted buffered input and a deferred approval response. This rule rejects active siblings and non-durable child approvals.

### The tool task supplies the stop signal

The actor retains the task returned by the current tool pipeline. After eligibility, the actor cancels that task's token. A task completion signal returns to the actor mailbox. The actor then passivates and acknowledges drain. The actor does not treat token cancellation alone as proof that work stopped.

The pipeline can report cancellation as a failed batch. The actor suppresses only the cancellation that belongs to this verified drain attempt. An unrelated failure keeps the existing failure path. A stale task signal cannot complete a newer batch.

### The journal remains the recovery source

The actor adds no new persisted event in this slice. The existing approval and result events restore the parked batch. A recovered approval response uses its recorded `TurnContextRecord`. An incomplete legacy context cannot grant broader authority.

### Schematic sequence

```text
graceful stop -> session actor marks drain requested
approval/result journal callback -> actor checks every call
if all unfinished calls await durable approval and local buffers are empty:
    actor cancels the current tool task
    tool task stops -> actor receives the stop signal
    actor passivates -> drain ack
else:
    current bounded drain path remains
```

The sequence omits ordinary policy checks and journal callbacks.

## Risks / Trade-offs

- A sibling can finish while the actor checks eligibility. The actor uses applied result events and mailbox order to keep the result before drain.
- A tool can ignore cancellation. The actor then gives no early acknowledgment. The global stop limit still applies.
- A resolved approval can race with drain. The actor excludes an unfinished resolved call and keeps the bounded path.
- An accepted buffered message has no durable admission record today. The actor excludes that state until the later input-admission slice.

## Migration Plan

This change needs no journal migration or configuration update. A rollback removes the fast path. Journaled approvals keep their current recovery behavior.
