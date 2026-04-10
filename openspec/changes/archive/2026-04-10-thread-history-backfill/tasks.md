## 1. Channel abstraction layer

- [x] 1.1 Define `IThreadHistoryFetcher` in `Netclaw.Actors.Channels` returning `IReadOnlyList<ChannelInput>` for a given `SessionId`
- [x] 1.2 Keep `ChannelInput` and `SendUserMessage` free of any backfill-specific flags — hydration stays adapter-internal

## 2. Slack history fetcher implementation

- [x] 2.1 Create `SlackThreadHistoryFetcher` using `ISlackApiClient.Conversations.Replies`
- [x] 2.2 Implement cursor-based pagination to fetch all thread replies
- [x] 2.3 Filter out the Netclaw bot's own messages and any other `bot_id` messages
- [x] 2.4 Download image attachments via `SlackFileDownloader` with bot-token auth
- [x] 2.5 Content-scan downloaded images through `IContentScanner`
- [x] 2.6 Return each historical message as `ChannelInput` in chronological order
- [x] 2.7 Skip per-message download/scan failures with a warning and continue
- [x] 2.8 Return an empty list on API-level errors (permission denied, server error) and log a warning

## 3. Slack binding actor as persistent actor

- [x] 3.1 Convert `SlackThreadBindingActor` to `ReceivePersistentActor` with `PersistenceId = "slack-thread-cursor-{sessionId}"`
- [x] 3.2 Define internal `CursorAdvanced(string CursorTs)` event record
- [x] 3.3 Recover `_cursorTs` from `CursorAdvanced` events on actor start
- [x] 3.4 Truncate the journal via `DeleteMessages(LastSequenceNr - 1)` every 10 persisted events
- [x] 3.5 Register `SlackThreadHistoryFetcher` in DI via `SlackChannelRegistrationExtensions` and expose it through `SlackGatewayDependencies`

## 4. Hydration and merge semantics

- [x] 4.1 In `HandleInboundAsync`, extract the event's `ts` and drop the event with `stale_event` telemetry when `ts ≤ _cursorTs`
- [x] 4.2 On the first non-stale inbound per runtime, call `IThreadHistoryFetcher.FetchThreadHistoryAsync` (`_threadHistoryHydrated` flag)
- [x] 4.3 Compute the gap of messages strictly after `_cursorTs` and strictly before the triggering event's `ts`
- [x] 4.4 Merge the gap into the triggering `ChannelInput` as a single `TextContent` framed with `[thread history — messages exchanged before this inbound event]` / `[end thread history]` delimiters
- [x] 4.5 Append gap image `DataContent` items and triggering-message `DataContent` items to the merged input
- [x] 4.6 Include sender attribution and UTC timestamp per gap message in the text block
- [x] 4.7 After a successful input-channel write, persist `CursorAdvanced(ts)` via `PersistAsync`
- [x] 4.8 Do not advance the cursor if the write fails — next inbound will retry hydration
- [x] 4.9 Set `_threadHistoryHydrated = true` so subsequent inbound events in the same runtime skip rehydration

## 5. Prompt injection gate on hydrated messages

- [x] 5.1 Evaluate each gap message through `IPromptInjectionDetector` in `EvaluateBackfillMessageAsync`
- [x] 5.2 Drop gap messages with `Risk = High` and log a structured warning with sender and message ids
- [x] 5.3 Drop gap messages when the detector throws and mark the build result as `BackfillDetectorUnavailable = true`
- [x] 5.4 Post `BackfillDetectorWarning` to the thread once per inbound event when any gap message was dropped due to detector failure

## 6. Testing

- [x] 6.1 Unit tests for `SlackThreadHistoryFetcher` — pagination, bot filtering, API error handling
- [x] 6.2 Integration test: first inbound merges full thread history into the triggering message as a single user turn
- [x] 6.3 Integration test: hydration runs at most once per runtime and runs again after restart
- [x] 6.4 Integration test: persisted cursor survives actor restart and subsequent inbound advances past it
- [x] 6.5 Integration test: replayed/out-of-order inbound event with `ts ≤ cursor` is dropped with `stale_event` telemetry
- [x] 6.6 Integration test: high-risk gap message is excluded and live message still reaches the session
- [x] 6.7 Integration test: detector failure causes the warning post and excludes the gap message

## 7. Spec and documentation sync

- [x] 7.1 Run `dotnet slopwatch analyze` — no new violations
- [x] 7.2 Sync delta specs to `openspec/specs/` via `/opsx-sync` before archiving
