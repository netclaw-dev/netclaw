## 1. Native Teams activity regression coverage

- [x] 1.1 Add a realistic MCP approval test that renders the card, attaches it to `MessageActivityInput`, and verifies native SDK serialization.
- [x] 1.2 Add tests for card-only text omission, ordinary text delivery, native typing serialization, callback values, supported card values, terminal cards, and size limits.

## 2. Teams transport correction and diagnostics

- [x] 2.1 Build approval messages without empty text and verify the production SDK activity path.
- [x] 2.2 Map card payload, activity, serialization, create, reply, and update failures to safe Teams-only reason codes.
- [x] 2.3 Log only the Teams failure stage and exception type, then verify no unsafe value reaches results or diagnostics.

## 3. Verification

- [x] 3.1 Run focused Teams tests and the broader channel containment suites.
- [x] 3.2 Run build, solution tests, Slopwatch, header, diff, and strict OpenSpec checks.
- [x] 3.3 Create a corrective PR to `dev` and record the owner live-smoke gate.
