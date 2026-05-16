## 1. Pass 7a — Value-object audit document

- [x] 1.1 Write `docs/spec/value-object-audit.md` — the full inventory of the
  ~70 raw-primitive protocol fields, each tagged wrap-with-existing,
  wrap-with-new, or leave-as-string, with the headline counts and the top-10
  highest-impact opportunities.
- [x] 1.2 In the audit doc, record the value-object design rules (`readonly
  record struct`, single `Value`, explicit-only conversion, validating
  constructor where a rule exists, named factories for constants, serializer
  mapping that preserves wire/disk bytes).
- [x] 1.3 Cross-link the audit doc from this change's `design.md` and from
  issue #994.
- [x] 1.4 Verify Pass 7a: markdownlint clean on the new doc.

## 2. Pass 5 — Primary-constructor migration (≤3 required props)

- [x] 2.1 Migrate the protocol/session records with ≤3 required properties to
  positional primary-constructor form: `CommandAck`, `CommandNack`,
  `PrepareForDaemonRestart`, `WarmSession`, `SetSessionPromptOverlay`,
  `SessionLogDiagnostic`, `SessionEnsureResultDto`, `ChatMessageDto`,
  `JoinSession`, `LeaveSession`.
- [x] 2.2 Migrate the LLM/session-message records: `LlmResponseDeltaReceived`,
  `LlmCallFailed`, `ProcessingWatchdogExpired`, `SpawnChildActorRequest`,
  `CompactionTriggered`, `MemoryCheckpointEnqueueResult`, `ApprovalEntry`,
  `ChannelSecurityContext`, `HeadlessOptions`, and the `SessionOutput` subtypes
  (`TextOutput`, `TextDeltaOutput`, `ThinkingOutput`, `ThinkingDeltaOutput`,
  `SessionTitleOutput`).
- [x] 2.3 Confirm no migrated type appears in the `NetclawProtobufSerializer`
  registration list; leave any wire-serialized record shape in property-init
  form.
- [x] 2.4 Fix callsite compiler errors from the positional shape and update
  affected tests.
- [x] 2.5 Verify Pass 5: `dotnet build Netclaw.slnx` clean, affected test
  projects green, `dotnet slopwatch analyze` no new violations,
  `./scripts/Add-FileHeaders.ps1 -Verify` passes.

## 3. Pass 6 — `required`-keyword pass (4+ props)

- [x] 3.1 Apply the `required` keyword to the logically-required properties of
  `ToolAuditEntry` (`Netclaw.Actors/Tools/IToolExecutor.cs`) and any other
  record the audit confirms still uses bare `init` for 4+ required properties.
- [x] 3.2 Fix construction-site compiler errors and update affected tests.
- [x] 3.3 Verify Pass 6: build clean, tests green, slopwatch clean, file
  headers verified.

## 4. Pass 7b — Wrap-with-existing cleanups

- [x] 4.1 Route the already-defined identifier value objects (`ToolCallId`,
  `ToolName`, `SessionId`, and — where they exist — `BackgroundJobId`,
  `ReminderId`, `MimeType`) through every protocol boundary that currently
  unwraps them to a primitive, per the audit's wrap-with-existing list.
- [x] 4.2 Route the existing enums (`TrustAudience`, `ChannelType`, memory
  enums) through fields that currently carry their wire string, excluding the
  two `ToolExecutionContext`/`RunSubAgent` audience fields already typed by
  issue #994.
- [x] 4.3 For every wrapped field on a `NetclawProtobufSerializer`-registered
  type, update the `NetclawProtoMapper` `ToProto`/`FromProto` mapping so the
  `.proto` field stays a primitive; do not edit `.proto` schemas.
- [x] 4.4 For every wrapped field on a JSON-persisted type, add or extend the
  `JsonConverter<T>` so the document stores the bare primitive.
- [x] 4.5 Add byte-equality round-trip tests for each touched
  serializer-registered and JSON-persisted type.
- [x] 4.6 Fix callsite compiler errors and update affected tests.
- [x] 4.7 Verify Pass 7b: build clean, tests green, slopwatch clean, file
  headers verified; behavioral review of any callsite that previously relied on
  an empty/null primitive.

## 5. Pass 7c — New value objects: `TrustBoundary`, `SenderId`, `AgentName`

- [ ] 5.1 Create `TrustBoundary` (`Netclaw.Configuration`): `readonly record
  struct`, validating constructor (non-empty, canonical form), explicit operator
  only, named static factories `Public`/`Personal`/`Team`/`TrustedInstance`
  replacing the `SecurityPolicyDefaults` magic constants.
- [ ] 5.2 Replace `string Boundary` with `TrustBoundary` across `MessageSource`,
  `ChannelInput`, `StartBackgroundJob`, `CancelBackgroundJob`,
  `QueryBackgroundJob`, `BackgroundJobDefinition`, `ActiveJobInfo`,
  `ReminderDefinition`, `RunSubAgent`, `ToolExecutionContext`, and the memory
  query args; update the serializer/JSON-converter mappings.
- [ ] 5.3 Create `SenderId` (`Netclaw.Actors.Protocol`/`Channels`): validating
  value object; replace `string SenderId` on `ToolInteractionResponse`,
  `ChannelInput`, `MessageSource`, `ConnectionIdentity`, `StartBackgroundJob`,
  `BackgroundJobDefinition`, `ChannelSecurityContext`, `SlackThreadInbound`, and
  adopted-context records; convert at channel ingress from `SlackUserId` /
  `DiscordUserId`.
- [ ] 5.4 Create `AgentName` (`Netclaw.Actors.SubAgents`): validating value
  object; replace `string` agent-name fields on `SubAgentDefinition`,
  `SubAgentResult`, `SubAgentNotification`, `SubAgentOutput`,
  `CompletedSubAgentRun`, `AcceptedSubAgentFinding`.
- [ ] 5.5 Update `NetclawProtoMapper` mappings and JSON converters for all
  touched serializer-registered and JSON-persisted types; add byte-equality
  round-trip tests.
- [ ] 5.6 Fix callsite compiler errors and update affected tests.
- [ ] 5.7 Verify Pass 7c: build clean, tests green, slopwatch clean, file
  headers verified; behavioral review of trust-boundary and sender-id callsites.

## 6. Pass 7d — Remaining new value objects

- [ ] 6.1 Create `ModelId` (`Netclaw.Configuration`) and replace `string`
  model-id fields on `GetModelCapabilities`, `ModelCapabilitiesResponse`,
  `CapabilityResolved`, `DiscoveredModel`.
- [ ] 6.2 Create `TurnNumber` and `TurnId` (`Netclaw.Actors.Protocol`); confirm
  both have real callsites before creating the second, then replace the ordinal
  / correlation fields on `TurnCompleted`, `DeliveryFailed`,
  `SessionSnapshot.EligibleDeliveryTurnNumber`, `SessionOutputDto.TurnNumber`.
- [ ] 6.3 Create `ApprovalOptionKey`, `ApprovalVerb`, `SkillName`,
  `WebhookEventType`, `WebhookDeliveryId`, `SourceScope`, `SourceKind` in their
  domain-owning namespaces and replace the corresponding primitive fields.
- [ ] 6.4 Update `NetclawProtoMapper` mappings and JSON converters for touched
  serializer-registered and JSON-persisted types; add byte-equality round-trip
  tests.
- [ ] 6.5 Fix callsite compiler errors and update affected tests.
- [ ] 6.6 Verify Pass 7d: build clean, tests green, slopwatch clean, file
  headers verified.

## 7. Pass 7e — Memory / sub-agent finding enum unwrap fixes

- [ ] 7.1 Tighten `MemoryProposal` to carry its existing enums (`MemoryClass`,
  `MemorySensitivity`, `MemoryRecallMode`, `MemoryProposalOperation`,
  `SubjectKind`) instead of wire strings.
- [ ] 7.2 Tighten `ObservedMemoryCheckpointPayload` to carry
  `CheckpointTriggerType` instead of a wire string, and `AcceptedSubAgentFinding`
  to carry its typed enum.
- [ ] 7.3 Update serializer/JSON mappings for the touched types; add
  byte-equality round-trip tests.
- [ ] 7.4 Fix callsite compiler errors and update affected tests.
- [ ] 7.5 Verify Pass 7e: build clean, tests green, slopwatch clean, file
  headers verified.

## 8. Cross-cutting verification and close-out

- [ ] 8.1 Confirm every `NetclawProtobufSerializer`-registered type touched by
  Passes 7b–7e has a passing byte-equality round-trip test, and that a legacy
  on-disk job/reminder document still deserializes unchanged.
- [ ] 8.2 Run the eval suite (`./evals/run-evals.sh`) if any tool-facing
  surface (`ToolExecutionContext`, tool definitions) changed in a way the eval
  triggers list covers; record the result.
- [ ] 8.3 Final `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1
  -Verify` across the whole change.
- [ ] 8.4 Run `/opsx-verify` against this change, then `/opsx-sync` and
  `/opsx-archive`.
