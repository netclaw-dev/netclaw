## 1. Drain and resume

- [x] 1.1 Build the restart reminder from the recorded authority of a turn that stopped after its tool batch, and verify with the `RestartDrainSpecs` rows `ModelCallAfterToolBatch`, `ModelCallDeepInToolLoop`, `ToolBatchBoundary`, `ToolLoopCompaction`, `CompactionReplay`, and `AutomationTurnAfterToolBatch`
- [x] 1.2 Post the notice when the reminder cannot resume the turn, and verify with the rows `TurnWithoutChannelType`, `BufferedInput`, `ToolStillRunning`, and `RestartNoticeRuleTests`
- [x] 1.3 Carry the authority in snapshot field 22, wait for the store before the drain acknowledges, and verify with `SerializationRoundTripTests.Restart_stopped_turn_survives_the_snapshot_and_a_recorded_turn_clears_it`
- [x] 1.4 Resume the stopped turn on the restart reminder with `CommandAck`, and verify that the resumed request pairs each tool call with its result and holds no reminder text

## 2. Documents

- [x] 2.1 Update `session-resume`, SPEC-011, `tool-authorization.md`, and the `netclaw-operations` skill, and verify that each names the stopped-turn resume
