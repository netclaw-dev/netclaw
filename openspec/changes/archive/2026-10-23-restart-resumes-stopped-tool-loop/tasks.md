## 1. Continuation input

- [x] 1.1 Journal one continuation input for an eligible stopped turn, and verify with the `RestartDrainSpecs` rows that resume after a tool batch
- [x] 1.2 Refuse the continuation for a live job and an overflow replay, and verify with the rows `LiveBackgroundJobAtDrain` and `CompactionReplay`
- [x] 1.3 Keep the continuation away from recovery and other requesters, and verify with the rows `NewInputBeforeReminder` and `SecondDeliveryAfterToolBatch`
- [x] 1.4 Add no snapshot field and no event type, and verify that the protobuf schema has no change

## 2. Gate and documents

- [x] 2.1 Add the `stopped-turn-continuation` Stryker gate, and verify 10 killed of 10
- [x] 2.2 Update `session-resume`, SPEC-011, `tool-authorization.md`, and the `netclaw-operations` skill, and verify that each names the continuation input
