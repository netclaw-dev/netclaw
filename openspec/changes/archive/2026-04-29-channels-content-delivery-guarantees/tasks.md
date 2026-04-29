## 1. Structured routing-policy ignore reasons

- [x] 1.1 Add `SlackRoutingIgnoreReason` enum to `SlackRoutingPolicy.cs` with values `NoContent`, `WrongKind`, `HiddenMessage`, `UnsupportedSubtype`, `DmNotAllowed`, `DmMentionRequired`, `ChannelMentionRequired`
- [x] 1.2 Convert `SlackRoutingDecision` from enum to `readonly record struct` with `Kind` and nullable `IgnoreReason`; add factory methods `Ignore(reason)`, `StartOrContinue()`, `ContinueOnly()`; add `SlackRoutingDecisionKind` enum for the kind
- [x] 1.3 Replace every `return SlackRoutingDecision.Ignore;` in `SlackRoutingPolicy.Evaluate` with `return SlackRoutingDecision.Ignore(<specific reason>);` — one reason per branch, matching D1/spec case enumeration
- [x] 1.4 Update `SlackConversationActor.cs:64-67` to read `decision.Kind`, log the structured `ignoreReason=<name>` alongside `reason=routing_policy_ignore`, and pass the reason string to `ChannelTelemetry.RecordSlackEventFiltered`
- [x] 1.5 Update `SlackRoutingPolicyTests` so every existing `Assert.Equal(SlackRoutingDecision.Ignore, decision)` instead asserts both `decision.Kind == Ignore` and the expected `decision.IgnoreReason`; add one new test per branch whose reason is not already covered (added: `HiddenMessage_IsIgnored`, `BlockActionKind_IsIgnoredAsWrongKind`)
- [x] 1.6 Add regression tests covering realistic Slack file-upload shapes that the existing suite did not cover: DM + `file_share` subtype + image, DM + files with no subtype (modern upload path), DM + hidden file_share (drop ordering). All three pass against current code, which narrows the 13:38 bug to a field on the incoming `MessageEvent` that Group 1's structured `ignoreReason` log will identify on next repro.
- [x] 1.7 `dotnet build` + `dotnet test --filter "FullyQualifiedName~Channels"` pass: 164 tests green (21 routing policy tests including the 5 new ones, plus all existing channel integration tests)

## Reductions from original plan

During implementation the user explicitly asked "how much of the rest of the
code in the plan is over-engineered?" — answer was "most of it." The scope was
reduced to Group 1 only.

**Reverted:**
- **Group 2 — ContentDeliveryInvariant**: Deleted `ContentDeliveryInvariant.cs`, `ContentDeliveryInvariantTests.cs`, and the `LlmSessionActor` wiring (field `_currentTurnInboundFileCount`, `VerifyContentDeliveryInvariant` method, call site, `mediaRefs.Count` assignment). Reason: the invariant runs at the session-actor LLM-invoke boundary but the 13:38 bug drops upstream at the Slack gateway, before the session actor ever exists. It was forward-looking regression protection for a bug class that duplicates existing `SlackFileFlowIntegrationTests` pipeline coverage. Net revert ~130 LOC.

- **Group 3 — InboundEventCapture + `SlackChannelOptions.CaptureRawEvents` + schema entry**: Deleted `InboundEventCapture.cs`, `InboundEventCaptureTests.cs`, the `SlackChannel.Handle` call sites, the `CaptureRawEvents` config property, and the JSON schema update. Reason: operator-facing workflow required running Netclaw connected to a live Slack workspace to seed fixture JSON, which isn't a reusable contributor workflow. Net revert ~150 LOC.

- **Group 4 — Fixture replay harness**: Skipped entirely. The original plan promoted three helper classes out of `SlackFileFlowIntegrationTests` and added a JSON fixture corpus via `LoadFixture`. Reason: (a) `SlackFileFlowIntegrationTests` + `SlackThreadBackfillIntegrationTests` already provide pipeline coverage downstream of the routing policy; (b) promoting helpers buys nothing until a second consumer exists; (c) JSON fixtures introduce a source-of-truth dispute (Slack docs vs captured reality vs SlackNet type layout) that the SlackNet decompile already resolves — the schema is inferable from SlackNet's public types.

- **Group 5 — Docs**: Skipped everything except the final slopwatch/test-suite runs. Most tasks documented the reverted `CaptureRawEvents` and its operator workflow; with capture gone, there is nothing to document.

**Final footprint**: Group 1 only, ~130 LOC production + tests, all already done.

## Final verification

- [x] `dotnet build` with `/p:NoWarn=NU1901` (pre-existing unrelated package vulnerability warning) succeeds
- [x] `dotnet test --filter "FullyQualifiedName~Channels"`: 164/164 green
- [ ] `dotnet slopwatch analyze` — no new violations (run in wrap-up commit)
- [ ] Verify `openspec validate channels-content-delivery-guarantees --type change` still passes after updating spec deltas to match reduced scope
