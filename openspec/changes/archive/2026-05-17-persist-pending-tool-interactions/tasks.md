## 1. Persist pending tool interactions

- [x] 1.1 Add `CallId` plus `Boundary` and `ChannelType` fields to the runtime
  `PendingToolInteraction` record in `LlmSessionActor.cs` and populate them in
  the `Command<ToolInteractionRequest>` handler
- [x] 1.2 Add nested `PendingToolInteractionRecord` and `ApprovalCandidateRecord`
  records plus a `PendingToolInteractions` list property to `SessionSnapshot.cs`
  (mirror `AdoptedContextSnapshotRecord`)
- [x] 1.3 Add `PendingToolInteractionProto` (+ nested `ApprovalCandidateProto`)
  and `repeated pending_tool_interactions` to `SessionSnapshotProto` in
  `netclaw_messages.proto` as an append-only field; regenerate proto code
- [x] 1.4 Extend `NetclawProtoMapper` `ToProto`/`FromProto` for `SessionSnapshot`
  with the pending-interaction mapping helpers
- [x] 1.5 Layer the pending set into `BuildSnapshot()`; restore it in
  `Recover<SnapshotOffer>`; clear it in `Recover<TurnRecorded>` and
  `Recover<SessionCompacted>`
- [x] 1.6 Save a snapshot immediately when an approval prompt is created
  (`Command<ToolInteractionRequest>`) to close the pre-snapshot crash window

## 2. Handle ToolInteractionResponse across phases

- [x] 2.1 Factor the tool-batch dispatch block out of `HandleToolCallResponse`
  into a shared `DispatchToolBatch` method
- [x] 2.2 Implement `RedriveToolBatchForApproval`: locate the tail assistant
  message with unanswered tool calls, rebuild `FunctionCallContent`s via
  `ChatMessageConverter.ToAiMessage`, transition `Ready → Processing`, dispatch
- [x] 2.3 Implement `HandleToolInteractionResponseWhenIdle`: requester
  `CanApprove` check, decision mapping, grant persistence for persistent scopes,
  `ApprovedOnce` context pre-seed, then re-drive
- [x] 2.4 Add `CommandAsync<ToolInteractionResponse>` to the `Ready` behavior
- [x] 2.5 Add `CommandAsync<ToolInteractionResponse>` to the `Passivating`
  behavior: abort passivation timers, transition to `Ready`, then handle
- [x] 2.6 Add a buffering `Command<ToolInteractionResponse>` to the `Compacting`
  behavior and replay the buffered response via `Self.Tell` after compaction

## 3. Fail loud on expired prompts

- [x] 3.1 When a response arrives for a call absent from `_pendingToolInteractions`
  and not reconstructable from `_state.History`, emit a user-visible `TextOutput`
  ("approval prompt expired — please re-issue the request") instead of a silent return

## 4. Tests

- [x] 4.1 Serialization round-trip test for `SessionSnapshot.PendingToolInteractions`
  covering all field types, plus a backward-compat case (proto with no field → empty list)
- [x] 4.2 Headline regression `Passivated_session_resumes_tool_batch_when_approval_arrives`
  (Akka.Hosting.TestKit): pending approval → passivate → cold respawn → response → re-drive
- [x] 4.3 Per-phase handler tests: `Ready` (deferred-idle), `Passivating`
  (abort + re-drive, no `Terminated`), `Compacting` (buffer + replay)
- [x] 4.4 Fail-loud test: cold session, unknown call id → expired-prompt
  `TextOutput`, no tool dispatch
- [x] 4.5 Authorization-parity test (non-requester response rejected) and
  denied-decision re-drive test
- [x] 4.6 Re-run `ApprovalChannelTests`, `SessionToolExecutionPipelineTests`,
  the session actor suite, and the proto-serializer manifest test

## 5. Quality gates and docs

- [x] 5.1 Run `dotnet slopwatch analyze` (no new violations) and
  `./scripts/Add-FileHeaders.ps1 -Verify`
- [x] 5.2 Review and update the `netclaw-operations` system skill if it
  documents approval prompt lifetime
- [x] 5.3 Run `/opsx-verify` against this change, then `/opsx-sync` and
  `/opsx-archive`
