## 1. Actor proof

- [x] 1.1 Add an actor test for a journaled approval during drain. Run it against the baseline and confirm that the drain ack does not arrive.
- [x] 1.2 Add the tool task stop signal and the durable approval gate. Verify that the actor test receives the ack after task stop.
- [x] 1.3 Cold-recover the session, approve the parked call, and verify one execution under the original trust context.

## 2. Negative boundaries

- [x] 2.1 Add a sibling tool gate and verify that an active sibling prevents an early drain ack.
- [x] 2.2 Verify that an unresolved result, non-durable approval, accepted buffer, or deferred response prevents the fast path.
- [x] 2.3 Run the focused actor tests and verify no duplicate tool action or incomplete journal result.

## 3. Contract and gates

- [x] 3.1 Update `SPEC-011` and the `netclaw-operations` system skill. Verify that both describe the approval-only drain rule.
- [x] 3.2 Run the behavioral eval suite for the skill change and verify its result.
- [x] 3.3 Run the actor test project, Slopwatch, header verification, OpenSpec validation, and `git diff --check`.
