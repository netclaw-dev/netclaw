## 1. Turn Context Model

- [x] 1.1 Add an internal turn authority context model with only durable execution/security fields.
- [x] 1.2 Add a persistence-safe turn context record for journaled approval events.
- [x] 1.3 Add focused tests for building turn context from live `MessageSource`.
- [x] 1.4 Document which `MessageSource` fields remain transport-only and are not part of turn context.

## 2. Session Approval State

- [x] 2.1 Add explicit approval turn state owned by `LlmSessionActor` beneath the coarse `SessionPhase` lifecycle.
- [x] 2.2 Record waiting approval state when a live tool approval request is emitted.
- [x] 2.3 Restore recovered waiting approval state during journal replay.
- [x] 2.4 Transition through redrive and abandoned states when approval responses or new user messages arrive.

## 3. Persistence And Compatibility

- [x] 3.1 Persist turn context with new `ToolApprovalRequested` events using additive protobuf fields only.
- [x] 3.2 Map persisted turn context into `PendingToolInteraction` without duplicating authority fields as the normal path.
- [x] 3.3 Add legacy compatibility restoration for pre-change approval events that only carry old trust fields.
- [x] 3.4 Add serialization and recovery tests for new and legacy approval events.

## 4. Redrive And Consumers

- [x] 4.1 Replace cold-recovery `MessageSource` synthesis as the normal authority restoration path.
- [x] 4.2 Project restored turn context into `ToolExecutionContext` for parked batch redrive.
- [x] 4.3 Keep restored turn context active through continuation LLM calls and continuation tool calls.
- [x] 4.4 Route memory recall, curation, and checkpoint audience/boundary/adopted-context decisions through turn context.
- [x] 4.5 Remove redundant redrive override parameters once turn context projection is authoritative.

## 5. Test Consolidation

- [x] 5.1 Keep end-to-end tests for recovered approval click, no duplicate prompt, sibling tool no-reexecution, wrong requester, and expired prompt behavior.
- [x] 5.2 Replace field-by-field cold-recovery tests with focused turn-context construction, persistence, restoration, and projection tests.
- [x] 5.3 Add memory safety regression coverage for recovered turns with third-party adopted context.
- [x] 5.4 Add a test proving shared context model does not include session-only or sub-agent-only lifecycle state.

## 6. Validation

- [x] 6.1 Run targeted actor approval recovery and serialization tests.
- [x] 6.2 Run `dotnet slopwatch analyze`.
- [x] 6.3 Run `./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 6.4 Run `openspec validate redesign-session-approval-state-machine --strict`.
