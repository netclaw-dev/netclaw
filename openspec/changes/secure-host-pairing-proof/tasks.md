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

- [x] 6.1 Use a dedicated host client that ignores remote client state, bearer tokens, HTTP proxies, and redirects.
- [x] 6.2 Verify the proof never reaches a remote endpoint, redirect target, or HTTP proxy.
- [x] 6.3 Restrict pairing-code mutation to the coordinator and expire replay entries with their proof windows.
- [x] 6.4 Bound local-control request load and verify key-ring first use and corruption failures.

## 7. Pull Request Review Follow-up

- [x] 7.1 Clarify credential lifetimes, recovery steps, glossary terms, and the forwarded-loopback boundary.
- [x] 7.2 Add status-specific pairing CLI guidance and tests that prove failures do not persist credentials.
- [x] 7.3 Update the operations guide and public delivery text.
- [x] 7.4 Run all required checks and answer the pull request review threads.

## 8. Exposure-Mode Review Clarification

- [x] 8.1 Document the two pairing paths and the authority matrix for every exposure mode.
- [x] 8.2 Add positive and negative reverse-proxy examples, including the copied-proof transport limit.
- [x] 8.3 Sync the main specifications and rerun strict OpenSpec validation.
- [x] 8.4 Add direct regression tests for non-loopback host access and forwarded-loopback denial.

## 9. Final Adversarial Review Fixes

- [x] 9.1 Reserve a valid pairing code before the registry write and consume that reservation after the write.
- [x] 9.2 Reject proof replay at the exact proof-lifetime boundary with deterministic virtual-time tests.
- [x] 9.3 Require HTTPS for remote exchange, reject redirects, bound error bodies, and handle invalid remote responses.
- [x] 9.4 Remove invalid exchange-result states, use `Task` for I/O, and report the actual failed host endpoint.
- [x] 9.5 Sync the main specs and rerun tests, evals, Slopwatch, headers, strict validation, and CRAP analysis.
