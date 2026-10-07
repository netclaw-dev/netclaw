## 1. Durable input

- [x] 1.1 Persist accepted input before acknowledgment.
- [x] 1.2 Store pending input and recent source IDs in snapshots.
- [x] 1.3 Close input from completed, failed, and tool started turns.
- [x] 1.4 Verify journal, snapshot, order, and source retry behavior.

## 2. Graceful drain

- [x] 2.1 Retain and cancel the active model task after a short grace.
- [x] 2.2 Return one standard reminder definition for eligible pending input.
- [x] 2.3 Exclude approvals, tool work, partial replies, and input without a channel type.

## 3. Existing reminder path

- [x] 3.1 Store reminder definitions in the restart manifest.
- [x] 3.2 Register fresh reminders through the reminder manager after startup.
- [x] 3.3 Restore pending input under its original context when the reminder arrives.
- [x] 3.4 Verify expiration, duplicate registration, and a cold session wakeup.

## 4. Verification

- [x] 4.1 Update SPEC-011 and the operations skill.
- [x] 4.2 Run actor and daemon tests, evals, Slopwatch, headers, and OpenSpec validation.
