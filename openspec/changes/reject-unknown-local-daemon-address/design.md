## Context

See [the proposal](proposal.md) and [the engineering glossary](../../../docs/spec/GLOSSARY.md).
PRD-004 CLI-012 and the existing daemon lifecycle contract control this repair.
The local resolver currently omits unreadable sources through a lenient loader.
The stop and readiness paths create their own local HTTP clients.

## Goals / Non-Goals

The repair rejects an unknown destination before transport creation.
It preserves remote endpoint selection, process identity proof, and valid default configuration semantics.
It changes no actor message, journal record, grant, authentication mode, or durable state.

## Decisions

### Resolve the local address from readable canonical sources

The local resolver reuses the existing preflight diagnostics and daemon source order.
The source order remains config, secrets, then environment.
An unreadable source leaves the local address unknown because that source can override its address.
The repair does not infer a destination from the remaining sources after a read failure.

`DaemonApi` owns local endpoint resolution. The loader supplies the canonical configuration.
`DaemonManager` owns the notice and the existing stop proof decision.
The readiness path reports an absent endpoint for the genuine unknown state.
Each decision uses call-local data. The repair persists no address cache.

### Keep process proof independent of an unknown address

The following flow is schematic. Existing PID validation, cancellation, supervisor, and signal steps surround it.

```text
resolve the local address
if a source cannot be read:
    report an unknown address
    notice outcome = Unconfirmed
else:
    send the notice through the local transport
    retain the existing answer classification

proof = notice outcome is Accepted
     or (notice outcome is Unconfirmed and the process serves this home)
signal only when proof exists
```

An unknown address supplies no additional authority.
The held-lock rule from status does not replace the stricter stop proof.
A known endpoint refusal still prevents the signal.

### Isolate every endpoint in tests

Required internal HTTP factory delegates reuse the existing local handler and timeout in production.
The fake test transport handles every URI, including a default address that an unsafe mutation selects.
The tests count client creation and request sends.
An unreadable-source row requires zero of both counts.
The existing process-test endpoint guard remains intact.

## Risks / Trade-offs

- A platform without readable process home data can refuse a stop when the address is unknown. This preserves the existing authority rule.
- A secrets read failure can prevent local control despite a valid config port. The secrets source can override that port.
- A failed address must not appear as the default URL in a readiness or refusal diagnostic.

## Migration Plan

The patch needs no data migration.
The operator repairs or restores the unreadable source to recover address-based control.
The private advisory patch receives local tests, mutation proof, and an independent review before public delivery.
