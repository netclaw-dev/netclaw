## 1. ACL Observe Outcome

- [ ] 1.1 Add `AclOutcome` enum (Allow, Observe, Deny) in `Netclaw.Channels`
- [ ] 1.2 Add `AclOutcome Outcome` to `IAclDecision`; keep `IsAllowed` as `Outcome == Allow`
- [ ] 1.3 Add `Observe` factory method to `SlackAclDecision`
- [ ] 1.4 Add `Observe` factory method to `DiscordAclDecision`
- [ ] 1.5 Unit tests: Observe decision has `IsAllowed = false` and correct Principal

## 2. ACL Observe Evaluation

- [ ] 2.1 Add `bool threadExists` parameter to `SlackAclPolicy.EvaluateInbound`; return Observe when AllowedUserIds blocks but threadExists is true
- [ ] 2.2 Add `bool threadExists` parameter to `DiscordAclPolicy.EvaluateInbound`; same logic
- [ ] 2.3 Update `SlackConversationActor` call site to pass `threadExists`
- [ ] 2.4 Update `DiscordConversationActor` call site to pass `threadExists`
- [ ] 2.5 Unit tests: non-allowed user + thread exists => Observe; non-allowed user + no thread => Deny; empty AllowedUserIds => Allow

## 3. IsObserver Flag Propagation

- [ ] 3.1 Add `bool IsObserver` to `ChannelInput` (default false)
- [ ] 3.2 Add `bool IsObserver` to `SlackThreadInbound`
- [ ] 3.3 Add `bool IsObserver` to `DiscordThreadInbound`
- [ ] 3.4 `SlackConversationActor`: set `IsObserver = true` when ACL outcome is Observe, forward to binding actor
- [ ] 3.5 `DiscordConversationActor`: same
- [ ] 3.6 `SlackThreadBindingActor`: propagate `IsObserver` from `SlackThreadInbound` to `ChannelInput`
- [ ] 3.7 `DiscordSessionBindingActor`: propagate `IsObserver` from `DiscordThreadInbound` to `ChannelInput`

## 4. Speaker Attribution Tags — Hydrated History

- [ ] 4.1 Change `ThreadHistoryContentMerger.MergeHistoryWithLiveContents` signature to accept `IReadOnlySet<string>? allowedUserIds`
- [ ] 4.2 Change tag format from `<user: {SenderId}, {ReceivedAt}>` to `<speaker: {SenderId}, role={role}, {ReceivedAt}>`
- [ ] 4.3 `SlackThreadBindingActor`: pass `AllowedUserIds` as HashSet when invoking merger (null when empty)
- [ ] 4.4 `DiscordSessionBindingActor`: same
- [ ] 4.5 Unit tests: merger with allow-list classifies roles; merger without allow-list defaults to authorized

## 5. Speaker Attribution Tags — Live Messages

- [ ] 5.1 `ChannelPipeline.MapToCommand`: prepend `<speaker: {SenderId}, role={role}>` to `SendUserMessage.Content`
- [ ] 5.2 Unit tests: authorized user gets `role=authorized`; observer gets `role=observer`

## 6. System Prompt Multi-Speaker Overlay

- [ ] 6.1 Define shared multi-speaker overlay text constant in `Netclaw.Channels`
- [ ] 6.2 `SlackThreadBindingActor.BuildOptions`: set `PromptOverlay` when `AllowedUserIds` is non-empty
- [ ] 6.3 `DiscordSessionBindingActor.BuildOptions`: same
- [ ] 6.4 Unit tests: overlay set when AllowedUserIds non-empty; null when empty

## 7. Spec Sync

- [ ] 7.1 Archive delta specs to main specs via `/opsx-sync`
- [ ] 7.2 Run `dotnet slopwatch analyze` — no new violations

## 8. Verification

- [ ] 8.1 All existing tests pass (`dotnet test`)
- [ ] 8.2 Manual verification: non-allowed user message in active Slack thread reaches session as observer
- [ ] 8.3 Manual verification: speaker tags visible in LLM context on both hydrated and live messages
