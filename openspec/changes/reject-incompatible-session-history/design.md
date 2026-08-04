## Context

The session actor persists media references with each chat message.
The message assembler later restores those references as model input.
The current ingress check only examines media on the new user command.
A model change can therefore make recovered history incompatible with the active model.

The routing chat client treats request failures as provider failures.
The actor must reject incompatible input before that boundary.

## Goals / Non-Goals

**Goals:**

- Check the complete active session input before every model call.
- Reject current, recovered, and tool-produced unsupported media.
- Fail closed for an unknown persisted media modality.
- Keep provider fallback and health signals out of this local error path.

**Non-Goals:**

- Convert media to another modality.
- Select another model.
- Change the persisted media format.
- Add audio or video support.

## Decisions

### The session actor owns the compatibility check

The actor has the active model capabilities and the canonical session history.
It will check persisted media references before it calls `IChatClient`.

The provider client was rejected as the owner.
That location cannot separate local input errors from provider failover without wider routing changes.

### One pure check covers all media references

A pure helper will map each `MediaModality` value to a `ModelModality` flag.
The result will list required, unsupported, and unknown modalities.

The actor will use the helper before it accepts a new user turn.
The actor will use it again before each model call after a tool result.

### The actor will reject instead of removing content

The actor will not remove an unsupported media reference.
It will emit an input compatibility error with the active model and missing modalities.

This choice preserves the session record and prevents silent context loss.

### Local compatibility errors will not enter model routing

The actor will complete a rejected new command without a provider call.
If a tool adds incompatible media, the actor will fail the current turn before the next model call.
Neither path will persist a provider failure or activate fallback.

## Risks / Trade-offs

- [A historical session remains unusable with a text-only model] -> The error names the required modalities and gives model-selection guidance.
- [A corrupt modality value exists in storage] -> The check rejects the call and reports an unknown modality.
- [A future call path bypasses the ingress check] -> The second check at the model-call boundary remains authoritative.

## Migration Plan

The change needs no data migration.
Deployment changes only the result for an incompatible session.
A rollback restores the old provider-error behavior.

## Open Questions

None.
