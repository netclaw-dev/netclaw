## 1. Slack history fetcher

- [x] 1.1 Remove the unconditional bot-message skip in
  `src/Netclaw.Channels.Slack/SlackThreadHistoryFetcher.cs`
  (was lines 154-155). Derive a sender id with user-id preference
  and bot-id fallback; drop entries with neither.
- [x] 1.2 Update `ConvertMessageAsync` to accept the derived sender id
  as an explicit parameter and use it for `ChannelInput.SenderId`.
- [x] 1.3 Add an explanatory comment on the new sender-id derivation
  block referencing the watermark dedup primitive.

## 2. Slack history fetcher tests

- [x] 2.1 Replace the existing `Filters_out_bot_messages` test in
  `src/Netclaw.Actors.Tests/Channels/SlackThreadHistoryFetcherTests.cs`
  with `Includes_bot_messages_with_bot_id_as_sender_when_user_is_missing`
  asserting the new inclusion behavior.
- [x] 2.2 Add `Prefers_user_id_over_bot_id_when_both_are_present`
  covering the user-id-first derivation.
- [x] 2.3 Acceptance: `dotnet test --filter
  FullyQualifiedName~SlackThreadHistoryFetcherTests` passes (11 tests,
  including the new ones).

## 2a. Discord history fetcher

- [x] 2a.1 Remove the three `IsBot` filter sites in
  `src/Netclaw.Channels.Discord/Transport/DiscordThreadHistoryFetcher.cs`:
  the main page-iteration filter, the thread-root resolution filter,
  and the raw-message-fetch inner filter.
- [x] 2a.2 Add explanatory comments at each site referencing the
  watermark dedup primitive and the unchanged inbound bot-message
  filter in `DiscordConversationActor`.
- [x] 2a.3 Add
  `Includes_bot_authored_messages_in_history_result` to
  `src/Netclaw.Actors.Tests/Channels/DiscordThreadHistoryFetcherTests.cs`.
- [x] 2a.4 Acceptance: `dotnet test --filter
  FullyQualifiedName~ThreadHistoryFetcher` passes for both Slack and
  Discord (18 tests).

## 3. Spec finalization

- [ ] 3.1 Run `openspec validate add-proactive-channel-sessions-conformance`
  and confirm the spec delta validates against the existing
  `thread-history-backfill` capability.
- [ ] 3.2 Update issue #953 with a comment noting that the Discord
  history fetcher must apply the same rule, referencing
  `openspec/changes/add-proactive-channel-sessions-conformance/specs/thread-history-backfill/spec.md`.

## 4. Optional follow-ups (deferred)

- [ ] 4.1 Eval suite regression case for proactive-post amnesia: seed
  the production-repro user reply, assert the LLM response references
  the seeded content and does not confabulate origin reasoning. Per
  CLAUDE.md eval-trigger list, context-assembly changes warrant
  coverage. Optional because the change does not modify context
  assembly itself — only what the fetcher returns to it.
- [ ] 4.2 Cross-channel conformance test base (e.g.,
  `IThreadHistoryFetcherConformance`) — defer until a second channel
  implements proactive posts (issue #953 for Discord).
- [ ] 4.3 Explicit "assistant-role" tagging in the adopted-context
  renderer for the agent's own sender id — defer until eval signal
  shows the sender-id-only framing is insufficient.

## 5. Manual smoke test

- [ ] 5.1 In a sandbox Slack workspace with the dev daemon attached,
  create a short-interval reminder that DMs the operator (mirrors the
  production repro shape).
- [ ] 5.2 Wait for the reminder to fire and post into Slack. Reply.
- [ ] 5.3 Verify the agent's reply references the original posted
  content (no off-topic confabulation). Inspect daemon logs for the
  `Fetched {Count} thread history messages` line and confirm
  `Count >= 1` (the bot's own message is now in the result).

## 6. Quality gates

- [ ] 6.1 Run `dotnet slopwatch analyze` and confirm no new
  violations.
- [ ] 6.2 Run `./scripts/Add-FileHeaders.ps1 -Verify` and confirm
  copyright headers (no new files added, but verify regression-free).
- [ ] 6.3 Build full solution: `dotnet build` from repo root.
