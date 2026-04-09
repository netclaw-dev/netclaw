## 1. Channel abstraction layer

- [x] 1.1 Add `IsBackfill` flag to `ChannelInput` (default `false`)
- [x] 1.2 Propagate `IsBackfill` through `ChannelPipeline.MapToCommand` to `SendUserMessage`
- [x] 1.3 Define `IThreadHistoryFetcher` interface in `Netclaw.Actors.Channels` returning `IReadOnlyList<ChannelInput>`

## 2. Slack history fetcher implementation

- [x] 2.1 Create `SlackThreadHistoryFetcher` implementing `IThreadHistoryFetcher` using `ISlackApiClient.Conversations.Replies`
- [x] 2.2 Implement pagination (cursor-based) to fetch all thread replies
- [x] 2.3 Filter out bot messages (Netclaw bot ID and any other `bot_id`)
- [x] 2.4 For each message: download image attachments via `url_private_download` with Bearer token auth
- [x] 2.5 Content-scan downloaded images through existing `IContentScanner` pipeline
- [x] 2.6 Assemble each historical message as `ChannelInput` with `IsBackfill = true`, including `TextContent` and `DataContent` for images
- [x] 2.7 Handle per-message errors gracefully (skip failed downloads/scans, log warning, continue)
- [x] 2.8 Handle API-level errors (permission denied, server error) — log warning, return empty list

## 3. Slack adapter integration

- [x] 3.1 Register `SlackThreadHistoryFetcher` in DI via `SlackChannelRegistrationExtensions`
- [x] 3.2 Inject fetcher into `SlackThreadBindingActor` via `SlackGatewayDependencies`
- [x] 3.3 In `EnsureInitializedAsync`, after pipeline creation and before unstash: call fetcher for new sessions only
- [x] 3.4 Write backfilled `ChannelInput` items to the input queue in chronological order before the triggering message
- [x] 3.5 Detect new-session vs recovery — skip backfill when session recovers from persistence

## 4. Session context injection

- [x] 4.1 Handle backfill-flagged `SendUserMessage` in session actor — accumulate rather than triggering LLM turn
- [x] 4.2 Assemble accumulated backfill messages into thread history context block with `[thread history]` delimiters
- [x] 4.3 Include sender attribution and UTC timestamp per message in the context block
- [x] 4.4 Include multimodal content (images as `DataContent`) inline in the context block
- [x] 4.5 Insert context block into conversation history before the first live user turn
- [x] 4.6 Persist the backfill context block as part of session state (survives compaction/recovery)

## 5. Testing

- [x] 5.1 Unit test `SlackThreadHistoryFetcher` — pagination, bot filtering, error handling
- [x] 5.2 Unit test `ChannelInput.IsBackfill` propagation through `MapToCommand`
- [x] 5.3 Integration test: backfill with mixed text+image messages flows through pipeline to session
- [x] 5.4 Integration test: new session triggers backfill, recovered session skips it
- [x] 5.5 Integration test: backfill context appears before live message in LLM context assembly

## 6. Spec and documentation sync

- [x] 6.1 Update `netclaw-operations` system skill if thread backfill introduces operator-visible behavior
- [x] 6.2 Run `dotnet slopwatch analyze` — no new violations
