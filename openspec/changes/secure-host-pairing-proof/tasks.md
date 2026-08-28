## 1. Planning and Shared Contract

- [x] 1.1 Add the approved security task to `IMPLEMENTATION_PLAN.md` and verify its PRD and OpenSpec links.
- [x] 1.2 Add the local-control proof term to the glossary and validate all OpenSpec artifacts.
- [x] 1.3 Add the shared Data Protection proof codec and verify binary layout, purpose isolation, and key-ring failures.

## 2. Daemon Security Path

- [x] 2.1 Add the bounded proof validator and verify time, operation, version, replay, and capacity decisions with virtual time.
- [x] 2.2 Add the local-control HTTP endpoint and verify every denial creates no pairing code.
- [x] 2.3 Remove hub code generation and verify the hub keeps only its authenticated chat functions.

## 3. Pairing State Integrity

- [x] 3.1 Add one pairing coordinator and verify code generation and exchange use the same serialized state boundary.
- [x] 3.2 Preserve a valid code after duplicate-name and registry failures, then verify a later unique-name exchange succeeds.
- [x] 3.3 Verify concurrent use permits one device registration and one code consumption.

## 4. CLI and Compatibility

- [x] 4.1 Replace the CLI SignalR call with the local-control endpoint and verify clear mixed-version errors.
- [x] 4.2 Verify a host without a device token can create a code in every exposure mode.
- [x] 4.3 Verify the container procedure uses the CLI inside the daemon container with the shared Netclaw home.

## 5. Regression Proof and Operations

- [x] 5.1 Add and approve the complete pairing security matrix snapshot.
- [x] 5.2 Extend the deterministic pairing smoke scenario and verify host success, remote denial, restart state, and duplicate-name retry.
- [x] 5.3 Update the operations skill and record the vague website procedure task for the next `0.27` beta.
- [x] 5.4 Run focused tests, the full suite, evals, Slopwatch, header checks, OpenSpec validation, and `git diff --check`.

## 6. Adversarial Review Follow-up

- [ ] 6.1 Use a dedicated host client that ignores remote client state, bearer tokens, HTTP proxies, and redirects.
- [ ] 6.2 Verify the proof never reaches a remote endpoint, redirect target, or HTTP proxy.
