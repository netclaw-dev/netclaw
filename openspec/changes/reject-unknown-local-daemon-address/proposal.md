## Why

An unreadable local config can cause the CLI to infer the default daemon address.
Issue #2476 requires an explicit unknown-address result before local HTTP traffic.

## What Changes

- Reject unreadable local config sources before local control transport creation.
- Skip the shutdown notice and readiness probe when the address is unknown.
- Retain the existing process home proof before a stop signal.
- Preserve explicit remote client endpoint precedence for remote operations.
- Add isolated regression tests and a narrow mutation target.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `daemon-exposure`: require readable local sources before the CLI derives a local control address.

## Impact

PRD-004 CLI-012 supplies the lifecycle command requirement.
The change affects the local CLI resolver, shutdown notice, readiness diagnostic, and operations guidance.
It adds no configuration option, dependency package, persistence record, or remote protocol.
The scope excludes remote endpoint discovery and changes to process identity proof.

### Security and operational impact

The repair prevents local control traffic to an inferred destination after a config read failure.
An unknown address gives no HTTP identity proof. The existing process environment rule still controls any stop signal.
The patch remains in the private advisory workflow until the owner approves disclosure.
