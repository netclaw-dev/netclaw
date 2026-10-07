## Context

See [proposal.md](proposal.md) for motivation. The current execution path already has three separate identities: the durable Netclaw session identity, the provider-authored tool-call identifier, and, for child work, a sub-session identity. None represents one authorization lifecycle. Policy logs often occur below the actor that knows the session and call identifiers, while approval prompts can be persisted and resumed after the original in-memory execution context is gone.

The terms **tool call**, **authorization attempt**, **main session**, **subagent**, **durable**, and **ephemeral** follow the [engineering glossary](../../../docs/spec/GLOSSARY.md).

## Goals / Non-Goals

**Goals:**

- Make one authorization lifecycle queryable without matching command, argument, or result text.
- Keep one identity across live approval retry, bridged child approval, and cold recovery.
- Keep the correlation value outside the model-visible tool contract and outside authorization decisions.
- Read old journal entries safely.

**Non-Goals:**

- Do not change which calls are allowed, denied, corrected, or prompted.
- Do not use telemetry identity to replace provider call identity or approval response correlation.
- Do not add arguments, results, paths, or requester data to the new logs.
- Do not create a distributed tracing dependency.

## Decisions

### Use a random, opaque value object

`AuthorizationAttemptId` is an internal immutable value object. A new value is random, opaque, and formatted consistently for structured logs. It has no embedded timestamp, session, call, path, or user data.

The value is created by the owner of the logical call before authorization begins. The execution context carries it to the dispatcher and policy logger. The value object prevents accidental confusion with `ToolCallId`, while its string representation is used in additive persistence fields.

Alternative considered: reuse the provider `CallId`. This fails because parent approval bridging can create a parent-scoped call identifier, and provider identifiers are not a Netclaw-owned privacy or uniqueness contract.

Alternative considered: use the distributed trace identifier. A trace can cover several tool calls or be absent after recovery, so it does not define this lifecycle.

### Treat identity as metadata, never capability

Authorization code may read the identifier only to log or carry it. Grant lookup, one-time approval snapshots, approval response matching, and policy evaluation continue to use their existing inputs.

```text
AuthorizationAttemptId -----> structured telemetry
          |
          +---------------> pending-approval persistence

AuthorizationAttemptId -X-> grant lookup
AuthorizationAttemptId -X-> approval response lookup
AuthorizationAttemptId -X-> allow or deny result
```

This makes a malformed identifier an observability defect, not a security bypass.

### Generate once at each logical call owner

The main session creates one identifier per provider tool call and passes a call-to-attempt map into the execution batch. A live prompt and retry reuse the same per-call execution context. A cold redrive takes the identifier from pending approval state and passes it back into the reconstructed batch.

The sub-agent creates one identifier per child tool call. If parent approval is required, the bridge carries this identifier with the prompt. A new child execution context created for the approved retry is initialized with the same identifier.

```text
main session                              subagent
    | create A                                | create B
    v                                         v
tool start A                              child start B
    | policy A                                | policy B
    | prompt A                                | bridge prompt B
    | persist A                               | decision B
    | decision A                              | retry B
    | retry A                                 | result B
    v
result A
```

Counterexample: a model receives a correction for call `c1` and authors call `c2`. The runtime creates a new identifier for `c2`; it does not copy `c1` because this is a new authorization attempt.

### Persist the value additively with pending approval

The pending-approval request and resolution protobuf messages gain new optional string fields. New writes use the canonical value. Recovery accepts an absent legacy field and creates a new identifier for future events. A malformed stored value receives the same compatibility treatment; it never prevents recovery and never changes authority.

The recovery sequence is:

```text
read pending approval
  -> valid stored attempt id: reuse it
  -> absent or invalid id: create diagnostic replacement
  -> preserve original call id, candidate snapshot, options, and authority
```

Rollback is safe because older binaries ignore unknown protobuf fields. After rollback, correlation across a recovered approval can be incomplete, but authorization behavior is unchanged.

### Emit stable structured field names at actor and policy boundaries

New or amended lifecycle logs use `AuthorizationAttemptId`, `SessionId`, `CallId`, and `SubSessionId` when the boundary owns those facts. Correction telemetry also uses `RemediationCode`. The dispatcher receives sufficient call-local context to log policy outcomes without parsing message text.

No new telemetry field contains raw arguments, command text, result text, paths, or requester identity. Existing logs outside this change are not expanded.

## Risks / Trade-offs

- **Risk: an early return misses terminal telemetry** -> Route all `ToolCallResult` construction through a result value that carries the attempt identifier and assert the major return branches in tests.
- **Risk: live and cold retry generate different identifiers** -> Pass the identifier explicitly in the batch and persist it with the pending approval.
- **Risk: sub-agent retry creates a new execution context** -> Construct the retry context with the original identifier and cover the bridge path with tests.
- **Risk: identifier is mistaken for authority** -> Keep the type internal, exclude it from grant and response keys, and add a security counterexample to the spec.
- **Trade-off: legacy recovery starts correlation midway** -> Prefer incomplete diagnostic history over rejecting old durable state or inventing a false link.

## Migration Plan

1. Add the internal value object and additive protobuf fields.
2. Carry the identifier through parent, recovery, and sub-agent paths.
3. Add structured lifecycle fields and deterministic tests.
4. Deploy normally; no data migration is required.
5. Roll back by deploying the prior binary. Unknown protobuf fields remain compatible; only the new correlation telemetry is lost.
