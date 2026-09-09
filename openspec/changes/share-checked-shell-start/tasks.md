## 1. Shared launch

- [x] 1.1 Implement one checked start for all three modes. Verify the focused shell and job suites.
- [x] 1.2 Bind the queued invocation to current authority. Verify revoked grants, path changes, and exact request retention.

## 2. Lifecycle and delivery

- [x] 2.1 Verify one start, cancellation before launch, process tree exit, output, timeout, and detached lifetime.
- [x] 2.2 Update operational skill guidance and verify the spec with strict validation.
- [ ] 2.3 Run integrated tests, Slopwatch, headers, and applicable evals. Record each result and limitation.
- [x] 2.4 Run an independent aggressive review. Fix all confirmed findings and publish a labeled PR.

Task 2.3 remains open because behavioral evals need a provider type, endpoint, and model ID. CI remains a separate gate.
