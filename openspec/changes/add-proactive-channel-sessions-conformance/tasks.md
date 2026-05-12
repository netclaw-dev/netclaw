## 1. Protocol surface

- [ ] 1.1 Add `Message` field (full posted payload) and `MessageId`
  field (platform-assigned id) to `StartProactiveThread` in
  `src/Netclaw.Channels.Slack/SlackIngressMessages.cs`. Keep
  existing fields. Document the new fields in the type's XML doc
  comment.
- [ ] 1.2 Add a `ProactivePostSeed` provenance flag (or equivalent
  marker) to the transcript entry / `MessageSource` shape used by
  the session pipeline so that the persistence and dispatch paths
  can distinguish seeded entries from real LLM-produced turns. Place
  this in the appropriate shared types project (likely
  `Netclaw.Actors.Protocol` or `Netclaw.Channels`).
- [ ] 1.3 Acceptance: build succeeds; consumers of
  `StartProactiveThread` that don't yet populate the new fields
  produce compiler warnings or are updated in this slice.

## 2. Tool wiring

- [ ] 2.1 Update `SendSlackMessageTool.ExecuteAsync`
  (`src/Netclaw.Channels.Slack/Tools/SendSlackMessageTool.cs`) so
  that the constructed `StartProactiveThread` carries
  `args.Message` and the platform-assigned message id returned from
  `PostNewThreadAsync` (`SlackNewThread.ThreadTs` or
  equivalent — confirm the right id from the Slack outbound client
  response).
- [ ] 2.2 If the outbound client does not currently expose the
  posted-message id distinct from the thread id, extend
  `ISlackOutboundClient.PostNewThreadAsync` /
  `SlackOutboundClient.cs` to surface it. For Slack a new top-level
  post yields `ts == thread_ts == messageId`, but the spec wording
  separates the two for cross-channel clarity.
- [ ] 2.3 Acceptance: tool call success path passes the full payload
  and the message id through to the gateway.

## 3. Binding actor seeding

- [ ] 3.1 Modify `SlackThreadBindingActor.HandleProactiveThreadAsync`
  (`src/Netclaw.Channels.Slack/SlackThreadBindingActor.cs:159`) to:
  - Call `EnsureInitializedAsync()` as today.
  - Persist a transcript entry from `message.Message` and
    `message.MessageId` with role `assistant` and the
    `ProactivePostSeed` provenance flag.
  - Send `ProactiveThreadAck` to `Sender` only after the persistence
    completes.
- [ ] 3.2 Implement idempotency: before persisting, check whether an
  entry with the same `MessageId` already exists in the transcript
  for this session; if yes, skip the write and ack normally.
- [ ] 3.3 Surface persistence failures distinctly from ack-routing
  failures so the originating tool sees an error rather than silent
  success when the seed cannot be persisted.
- [ ] 3.4 Acceptance: TestKit unit test demonstrating seed → ack
  ordering and idempotent reseed (see § 5).

## 4. Output pipeline dispatch suppression

- [ ] 4.1 Locate the dispatch path in the session output pipeline
  (likely in `Netclaw.Actors` session pipeline / output subscriber
  fan-out) where `TurnCompleted` and `TurnRecorded` events are
  emitted, and add a check that short-circuits the event emission
  when the entry's provenance flag is `ProactivePostSeed`.
- [ ] 4.2 Verify the persistence side of the same path still records
  the entry to the transcript store; the suppression must be
  scoped to the dispatch / subscriber-notification side only.
- [ ] 4.3 Acceptance: subscriber-probe TestKit test (see § 5)
  confirms no events fire for seed writes while normal turns still
  fire events.

## 5. Slack-side conformance tests

- [ ] 5.1 Add `SlackProactiveBootstrapSeedTests` (or extend
  `SlackProactiveThreadTests` in
  `src/Netclaw.Actors.Tests/Channels/`) covering:
  - 5.1.a Bootstrap returns `ProactiveThreadAck` only after the
    transcript contains the seeded entry. Assert by probing the
    persisted transcript synchronously after the ack.
  - 5.1.b Subscriber probe receives no `TurnCompleted` /
    `TurnRecorded` for the seed; receives both for a subsequent
    real assistant turn.
  - 5.1.c Replayed `StartProactiveThread` with the same `MessageId`
    does not create a duplicate entry.
  - 5.1.d After stopping and recreating the binding actor for the
    same session id, the recreated actor's loaded transcript still
    contains the seed.
- [ ] 5.2 Use `Akka.TestKit` with a fake `ISlackOutboundClient` that
  returns deterministic `ts` / `threadTs` values. No real Slack
  network access required.
- [ ] 5.3 Acceptance: all four tests pass; no other test in the
  Slack channel test project regresses.

## 6. Shared conformance test base

- [ ] 6.1 Create an abstract test base class — proposed location
  `src/Netclaw.Channels.Tests/` (new project) or under
  `Netclaw.Tests.Utilities` — that codifies the four conformance
  assertions from § 5.1 against an abstracted "channel under test"
  surface. The base class accepts the channel's bootstrap protocol
  type, ack type, and a factory for the channel's binding actor as
  generic / virtual members.
- [ ] 6.2 Refactor the Slack-side tests in § 5 to subclass the new
  base where possible (keeping Slack-specific assertions in
  Slack-specific subclasses).
- [ ] 6.3 Acceptance: Slack subclass passes; the base is documented
  so that the Discord implementer (issue #953) can subclass when
  shipping `send_discord_message`.

## 7. Eval suite regression case

- [ ] 7.1 Add a new eval case under `evals/` named something like
  `proactive-post-amnesia-regression.yaml` (or matching the
  existing naming convention in that directory).
- [ ] 7.2 The case bootstraps a synthetic proactive-post session,
  injects the user reply from the production repro ("Well, that's
  only for NVIDIA, right?"), and asserts the LLM response:
  - References content from the seeded payload (positive signal —
    LLM has the anchor).
  - Does NOT mention any of the known-hallucinated topics from the
    repro that came from memory recall confusion (DGX Spark,
    NADDOD, MI350P, etc.).
  - Does NOT confidently claim reasoning about *why* the post was
    made (acceptable: "I don't have details from that earlier
    session"; unacceptable: confident confabulation).
- [ ] 7.3 Run `./evals/run-evals.sh` and confirm the new case passes;
  document the exact invocation in the change's smoke-test notes.
- [ ] 7.4 Acceptance: case is committed and passes locally; CI eval
  job (if present) passes on this branch.

## 8. Observability

- [ ] 8.1 Add a counter `proactive_session_seeded` with
  tags `channel` and `outcome` (success/failure) emitted from the
  binding actor's seed path. Wire it into the existing channel
  telemetry surface (`ChannelTelemetry.For(...)` or equivalent).
- [ ] 8.2 Add a structured log entry on seed write that includes
  `messageId`, `sessionId`, payload byte size, and outcome. Use
  the existing structured logger pattern in
  `SlackThreadBindingActor`.
- [ ] 8.3 Acceptance: counters and logs emit during the unit tests
  (verifiable via probe / log capture).

## 9. Spec cross-reference and OpenSpec finalization

- [ ] 9.1 Add a non-normative "See also: `proactive-channel-sessions`"
  reference in `openspec/specs/netclaw-slack-socket/spec.md` so the
  Slack spec points to the conformance authority.
- [ ] 9.2 Update issue #953 with a comment confirming the spec is
  available at `openspec/specs/proactive-channel-sessions/spec.md`
  and that any Discord implementation must satisfy the shared
  conformance test base.
- [ ] 9.3 Run `openspec verify --change add-proactive-channel-sessions-conformance`
  (or the equivalent OpenSpec verification command) to confirm
  the change artifacts validate before merge.

## 10. Manual smoke test

- [ ] 10.1 In a sandbox Slack workspace with the dev daemon attached:
  create a one-shot or short-interval reminder configured to DM the
  operator (DeliveryKind.None or equivalent that exercises
  `send_slack_message`).
- [ ] 10.2 Wait for the reminder to fire and post into Slack. Reply
  in the thread.
- [ ] 10.3 Verify in daemon logs that:
  - `proactive_session_seeded` counter fired with
    `outcome=success`.
  - The seed write log line contains the expected `messageId` and
    `sessionId`.
- [ ] 10.4 Verify the agent's reply to the user is on-topic with
  respect to the seeded payload (no DGX Spark / NADDOD / MI350P
  hallucinations).
- [ ] 10.5 Force a daemon restart between bootstrap and user reply;
  reply after restart and verify the seed survived (the agent's
  reply remains on-topic).

## 11. Documentation and post-merge follow-ups

- [ ] 11.1 Update `docs/spec/` or relevant runbook docs if the
  behavior is documented operator-facing anywhere; reference the
  new spec.
- [ ] 11.2 No system-skill changes required for this spec (per
  CLAUDE.md's system-skill mapping table — this work doesn't touch
  identity, memory, operations, search-citation, skill-authoring,
  or projects); confirm during code review.
- [ ] 11.3 Run `dotnet slopwatch analyze` and confirm no new
  violations introduced by the implementation slices.
- [ ] 11.4 Run `./scripts/Add-FileHeaders.ps1 -Verify` and confirm
  copyright headers on all new .cs files.
