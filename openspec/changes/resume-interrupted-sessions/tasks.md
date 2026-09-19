## 1. Durable input admission

- [x] 1.1 Add `InputAdmitted` and terminal input IDs to protobuf and journal mapping. Verify old and new event round trips.
- [ ] 1.2 Store the ordered pending input ledger in session snapshots. Verify replay across a snapshot boundary.
- [ ] 1.3 Persist ready, busy, and compacting input before each ack. Verify a journal failure rejects input.
- [x] 1.4 Restore source retry deduplication and exact input order. Verify a lost ack does not duplicate a stable source ID.
- [ ] 1.5 Consume admitted IDs in completed, tool-started, and failed turns. Verify replay cannot restart terminal work.

## 2. Graceful stop classification

- [ ] 2.1 Retain the active model task and add a short completion grace. Verify task cancellation completes before drain ack.
- [ ] 2.2 Return typed drain results for safe candidates and blocked work. Verify partial output, tool effects, and approvals stay quiet.
- [ ] 2.3 Save candidates from config and normal stop paths with an atomic manifest write. Verify a completed idle session has no candidate.

## 3. Short restart recovery

- [ ] 3.1 Restore channel output bindings after channel startup. Verify the binding ack precedes a resumed model call.
- [ ] 3.2 Validate the absolute deadline, input IDs, authority, and newer work inside the actor. Verify stale requests start no call.
- [ ] 3.3 Resume the original turn and one ordered queued call. Verify model input and trust context across cold recovery.
- [ ] 3.4 Retain blocked candidates and emit operator diagnostics. Verify a missing output route and an expired deadline stay quiet.

## 4. Documentation and gates

- [ ] 4.1 Update SPEC-011 and the operations skill. Verify the skill version and behavior match the actor contract.
- [ ] 4.2 Correct the daemon container spec and run an isolated persistent-home stop/start test.
- [ ] 4.3 Run actor and daemon suites, Spark2 evals, Slopwatch, header verification, and strict OpenSpec validation.
