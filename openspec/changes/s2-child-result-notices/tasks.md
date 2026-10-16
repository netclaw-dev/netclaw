## 1. Model and journal

- [x] 1.1 Replace `Prepared`, `Consumed`, and `Closed` with `Reported`; keep a read-only prepared pair on `Terminal`
- [x] 1.2 Add `ChildNotice` to `InputAdmitted` (proto field 14) and the reported summary to snapshot case 19
- [x] 1.3 Keep `ChildContinuation` and `ResultPrepared` read-only; fail any write

## 2. Runtime

- [x] 2.1 Write `task.md` before acceptance; remove the folder after a rejection; refuse a start without `file_read`
- [x] 2.2 Write `result.md` off the actor thread and admit one notice
- [x] 2.3 Classify the notice as automated input by kind
- [x] 2.4 Delete the delivery slot, the wake paths, the child precedence, and the synthetic pair
- [x] 2.5 Recover ended runs, keep the session for them, and wake a child-only session at restart
- [x] 2.6 Resume the leading inputs of one requester at restart

## 3. Verification

- [x] 3.1 Specs, own tests, and the focused Stryker targets pass
- [ ] 3.2 Run the evals for the `agent-coordination` skill change (due)
