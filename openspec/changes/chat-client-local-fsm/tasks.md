## 1. Daemon text admission

The [canonical plan](../../../.systematize/plans/chat-client-local-fsm/plan.html) owns task details and verification procedures.
Source PRDs: PRD-001, PRD-004, and PRD-009.
The user authorizes implementation. Complete each task after its proof passes.

- [x] 1.1 Complete [T1](../../../.systematize/plans/chat-client-local-fsm/plan.html#t1). Verify the real fixture reaches [V1](../../../.systematize/plans/chat-client-local-fsm/plan.html#v1)'s journal barrier.
- [x] 1.2 Complete [T2](../../../.systematize/plans/chat-client-local-fsm/plan.html#t2). Verify the early-response baseline fails V1's bounded observation.
- [x] 1.3 Complete [T3](../../../.systematize/plans/chat-client-local-fsm/plan.html#t3). Verify V1 confirms admission before success and before model completion.
- [x] 1.4 Complete [T4](../../../.systematize/plans/chat-client-local-fsm/plan.html#t4). Verify [V2](../../../.systematize/plans/chat-client-local-fsm/plan.html#v2) reports explicit failure without false admission.
- [x] 1.5 Complete [T4a](../../../.systematize/plans/chat-client-local-fsm/plan.html#t4a). Verify session responses advertise version 1 and old clients retain their contract.
- [x] 1.6 Complete [T5](../../../.systematize/plans/chat-client-local-fsm/plan.html#t5). Verify [V3](../../../.systematize/plans/chat-client-local-fsm/plan.html#v3) preserves work after disconnect.
- [x] 1.7 Complete [T6](../../../.systematize/plans/chat-client-local-fsm/plan.html#t6). Verify the glossary, contract, operator guidance, and skill describe admission accurately.

## 2. Local client actor

- [x] 2.1 Complete [T7](../../../.systematize/plans/chat-client-local-fsm/plan.html#t7). Verify [V4](../../../.systematize/plans/chat-client-local-fsm/plan.html#v4) and [V5](../../../.systematize/plans/chat-client-local-fsm/plan.html#v5) preserve control and isolation.
- [x] 2.2 Complete [T8](../../../.systematize/plans/chat-client-local-fsm/plan.html#t8). Verify V4 preserves order, cancellation outcomes, and one transport operation.
- [x] 2.3 Complete [T9](../../../.systematize/plans/chat-client-local-fsm/plan.html#t9). Verify [V6](../../../.systematize/plans/chat-client-local-fsm/plan.html#v6) delivers the final close receipt.
- [x] 2.4 Complete [T10](../../../.systematize/plans/chat-client-local-fsm/plan.html#t10). Verify [V8](../../../.systematize/plans/chat-client-local-fsm/plan.html#v8) finds one lifecycle owner and both old paths absent.
- [x] 2.5 Complete [T11](../../../.systematize/plans/chat-client-local-fsm/plan.html#t11). Verify V4–V7 retain behavioral assertions, typed-key coverage, and native proof.
- [x] 2.6 Complete [T12](../../../.systematize/plans/chat-client-local-fsm/plan.html#t12). Verify V5 rejects unsupported admission versions and V8 completes quality and skill gates.

## 3. Combined evidence and closure

- [x] 3.1 Run [V1–V8](../../../.systematize/plans/chat-client-local-fsm/plan.html#verification) on the combined candidate. Obtain independent verification with exact revisions and pass counts.
- [ ] 3.2 Complete the [authorized handoff and retention steps](../../../.systematize/plans/chat-client-local-fsm/plan.html#handoff). Verify current architecture and retrievable evidence before archive.
