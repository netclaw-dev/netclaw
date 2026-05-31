# netclaw-slack-socket Specification

## Purpose

Define Slack transport behavior for Netclaw MVP using Slack Socket Mode.

## Requirements

### Requirement: Socket Mode transport

Netclaw SHALL use Slack Socket Mode as the primary transport for inbound and
outbound message handling in MVP. The Slack channel SHALL register a
Socket Mode connection for message events and approval replies. No inbound HTTP
endpoint SHALL be required for interactive approval responses.

#### Scenario: Socket session established

- **GIVEN** valid Slack app and bot tokens are configured
- **WHEN** Netclaw starts
- **THEN** it opens a Socket Mode connection
- **AND** reports connection health in operator diagnostics

#### Scenario: Approval replies received via Socket Mode message events

- **GIVEN** an active Socket Mode connection
- **WHEN** a user replies `A`, `B`, or `C` to an approval prompt in the thread
- **THEN** the Slack channel receives the reply as a Slack message event via WebSocket
- **AND** no HTTP endpoint is required

### Requirement: Thread-bound reply delivery

Netclaw SHALL post assistant responses into the same Slack thread that produced
the session command.

#### Scenario: In-thread conversation

- **GIVEN** an allowed sender posts in thread `T`
- **WHEN** the turn completes
- **THEN** Netclaw posts the reply in thread `T`

### Requirement: No required inbound public webhook

Netclaw SHALL not require a public inbound HTTP endpoint for base Slack
transport operation, including interactive approval responses.

#### Scenario: Local-only runtime

- **GIVEN** Netclaw runs with loopback-only binding
- **WHEN** Slack Socket Mode is connected
- **THEN** Slack interaction still functions for inbound and outbound messaging
- **AND** approval text replies are received via Socket Mode

### Requirement: Persistent per-thread cursor

`SlackThreadBindingActor` SHALL be a persistent actor with
`PersistenceId = "slack-thread-cursor-{sessionId}"`. It SHALL persist a single
piece of state — the Slack `ts` of the most recently successfully processed
inbound event for its thread — using an event-sourced `CursorAdvanced`
record. The cursor SHALL be advanced only after an inbound event has been
enqueued onto the session input channel. The actor SHALL truncate its
persistence journal by calling `DeleteMessages` every 10 persisted events to
keep storage bounded; only the latest cursor matters for recovery.

#### Scenario: Cursor persists across actor passivation

- **GIVEN** a thread has processed an inbound event with ts `1712700000.000100`
- **WHEN** the binding actor passivates after one hour of idle time
- **AND** a later inbound event arrives for the same thread
- **THEN** the recovered actor restores `_cursorTs = "1712700000.000100"`
  before processing the new event

#### Scenario: Cursor survives daemon restart

- **GIVEN** a thread has processed events up to cursor `1712700000.000500`
- **WHEN** the daemon restarts and a new inbound event arrives
- **THEN** the binding actor replays `CursorAdvanced` events from persistence
- **AND** the cursor is `1712700000.000500` before the new event is evaluated

#### Scenario: Journal is truncated periodically

- **GIVEN** the binding actor has persisted 10 `CursorAdvanced` events
- **WHEN** the 10th event is applied
- **THEN** the actor calls `DeleteMessages(LastSequenceNr - 1)`
- **AND** subsequent recovery replays only the latest event

### Requirement: Stale inbound event drop

Before enqueueing an inbound Slack event, `SlackThreadBindingActor` SHALL
extract the event's `ts` and compare it against the persisted cursor. If the
event's `ts` is at or before the cursor, the event SHALL be dropped without
being enqueued, and a `stale_event` telemetry counter SHALL be recorded. This
SHALL apply uniformly to Socket Mode replays and any out-of-order delivery.

#### Scenario: Replayed event after reconnect is dropped

- **GIVEN** the cursor is `1712700000.000500`
- **WHEN** Slack Socket Mode replays an inbound event with ts
  `1712700000.000400`
- **THEN** the binding actor drops the event
- **AND** records `ChannelTelemetry.RecordSlackEventDropped("stale_event")`
- **AND** the session input channel receives nothing

#### Scenario: New event advances past the cursor

- **GIVEN** the cursor is `1712700000.000500`
- **WHEN** a genuinely new inbound event with ts `1712700000.000600` arrives
- **THEN** the event is processed and enqueued
- **AND** the cursor is advanced to `1712700000.000600` after enqueue

### Requirement: Thread hydration on first inbound per runtime

When `SlackThreadBindingActor` is freshly initialized (including after daemon restart), the first non-stale inbound event SHALL trigger a single
thread hydration pass. The actor SHALL call
`IThreadHistoryFetcher.FetchThreadHistoryAsync`, compute the gap of messages
strictly after the cursor and strictly before the triggering event's `ts`,
and merge the surviving gap content into the triggering `ChannelInput`.
Hydration SHALL run at most once per actor runtime; subsequent inbound events
in the same runtime SHALL NOT re-fetch history.

#### Scenario: First inbound after restart hydrates the gap

- **GIVEN** a cursor of `1712700000.000500` persisted from a prior run
- **WHEN** the daemon restarts and a new inbound event with ts
  `1712700000.000900` arrives
- **THEN** the actor fetches full thread history once
- **AND** includes messages with ts strictly between `500` and `900` in the
  merged content
- **AND** sets `_threadHistoryHydrated = true`

#### Scenario: Subsequent inbound events skip rehydration

- **GIVEN** hydration has already run in this actor runtime
- **WHEN** a second inbound event arrives
- **THEN** `IThreadHistoryFetcher` is not invoked
- **AND** the event is enqueued as a normal message with its own content only

#### Scenario: Fresh thread hydration on first mention

- **GIVEN** no cursor has ever been persisted for this thread
- **WHEN** an `app_mention` inbound event arrives
- **THEN** the actor fetches the full thread history
- **AND** includes all messages with ts strictly before the mention event

### Requirement: Merge hydrated content into triggering ChannelInput

Hydrated gap content SHALL be merged directly into the triggering inbound
event's `ChannelInput` rather than delivered as separate messages. The merge
SHALL produce a single `ChannelInput` whose `Contents` contain:

1. One `TextContent` that begins with the header
   `[thread history — messages exchanged before this inbound event]`,
   contains one entry per gap message with sender attribution and a UTC
   timestamp, ends with `[end thread history]`, and is followed by the
   triggering message's live text.
2. Any image `DataContent` items from gap messages.
3. Any image `DataContent` items from the triggering message.

The session layer SHALL receive exactly one `SendUserMessage` for the
triggering event with no special handling.

#### Scenario: Single merged message reaches the session

- **GIVEN** a gap of 3 historical messages and 1 triggering mention
- **WHEN** hydration completes
- **THEN** exactly one `ChannelInput` is written to the input channel
- **AND** its first `TextContent` contains the `[thread history …]` block
  followed by the live mention text

#### Scenario: Historical images included as DataContent

- **GIVEN** a gap message has one image attachment
- **WHEN** the merge runs
- **THEN** the image bytes appear as a `DataContent` on the merged
  `ChannelInput`
- **AND** the text block records `[image attachments: 1]` for that entry

#### Scenario: Empty gap produces an unmerged inbound

- **GIVEN** the fetcher returns history but no messages fall strictly between
  the cursor and the triggering event
- **WHEN** the actor builds the merged input
- **THEN** the triggering event is enqueued with its original content only
- **AND** no `[thread history …]` block is added

### Requirement: Prompt injection gate on hydrated gap messages

Each gap message text SHALL be evaluated by `IPromptInjectionDetector` before
being merged. Messages whose detection result is `Risk = High` SHALL be
dropped from the merge and logged as a warning. If the detector itself throws
or otherwise fails for a gap message, that message SHALL be dropped and the
actor SHALL post a `BackfillDetectorWarning` reply to the thread so the user
is informed that some prior context was excluded.

#### Scenario: High-risk historical message excluded

- **GIVEN** a gap message contains a prompt-injection attack pattern
- **WHEN** the injection detector returns `Risk = High`
- **THEN** the message is dropped from the merge
- **AND** a warning is logged with sender and message identifiers
- **AND** the rest of the hydration continues

#### Scenario: Detector failure warns the user

- **GIVEN** the injection detector throws for a gap message
- **WHEN** the actor processes that message
- **THEN** the message is dropped from the merge
- **AND** the actor posts `BackfillDetectorWarning` to the thread exactly once
  per inbound event

### Requirement: Slack history fetch via conversations.replies

`SlackThreadHistoryFetcher` SHALL implement `IThreadHistoryFetcher` using
`ISlackApiClient.Conversations.Replies`. It SHALL paginate through all
replies, filter out the bot's own messages and any other messages carrying a
`bot_id`, download image attachments via `url_private_download` with
bot-token Bearer auth, and content-scan each image through `IContentScanner`.
Per-message download or scan failures SHALL be skipped with a warning. API-
level failures (permission denied, server error) SHALL return an empty list.

#### Scenario: Paginated fetch for long threads

- **GIVEN** a thread has more than 1000 messages
- **WHEN** the fetcher retrieves the thread
- **THEN** it paginates using the cursor returned by each response until no
  cursor remains
- **AND** returns all messages in chronological order

#### Scenario: Bot messages excluded

- **GIVEN** a thread contains messages from users, the Netclaw bot, and a CI bot
- **WHEN** the fetcher retrieves the thread
- **THEN** messages matching the Netclaw bot id are excluded
- **AND** messages carrying any other `bot_id` are excluded
- **AND** only human user messages remain

#### Scenario: API error does not block session creation

- **GIVEN** `conversations.replies` returns a permission error
- **WHEN** the fetcher runs
- **THEN** the fetcher logs a warning and returns an empty list
- **AND** the binding actor enqueues the triggering event with its original
  content only

- **AND** approval button clicks are received via Socket Mode

### Requirement: Approval prompt rendering via Block Kit

The Slack channel SHALL render `ToolInteractionRequest` outputs as approval
prompt messages in the session thread. The prompt SHALL include the tool name,
a description of what the tool wants to do, and available response options.
The channel SHALL support both Block Kit interactive buttons and text-based
ABC option lists as fallback rendering.

#### Scenario: Approval prompt posted in thread

- **GIVEN** the session emits a `ToolInteractionRequest` with `Kind=approval`
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts an approval prompt message in the session's thread
- **AND** the message shows the tool name, command, and response options

#### Scenario: Approval prompt for non-shell tool

- **GIVEN** the session emits a `ToolInteractionRequest` for an MCP tool
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts an approval prompt showing the tool name and description

### Requirement: Approval response routing to session

The Slack channel SHALL route approval responses back to the originating session
actor as `ToolInteractionResponse` messages. Responses MAY arrive via
`BlockAction` events (button clicks) or text message parsing (ABC options).

#### Scenario: User approves via text response

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies "A" or "approve once"
- **THEN** the Slack channel parses the response
- **AND** sends a `ToolInteractionResponse` with `approve_once` to the session

#### Scenario: Approval response from non-existent session ignored

- **GIVEN** an approval response references a session that no longer exists
- **WHEN** the routing is attempted
- **THEN** the event is silently discarded

### Requirement: Approval prompt rendering via text reply flow

The Slack channel SHALL render `ToolInteractionRequest` outputs as in-thread
text prompts. For approval-type interactions, the prompt SHALL present four
reply options: `A` = Approve Once, `B` = Approve For This Chat, `C` = Approve
Always, and `D` = Deny. The
message SHALL include the tool name and a display of what the tool wants to do
(e.g., the shell command).

#### Scenario: Approval prompt posted with A/B/C/D text options

- **GIVEN** the session emits a `ToolInteractionRequest` with `Kind=approval`
- **WHEN** the Slack subscriber receives the output
- **THEN** it posts a text message in the session's thread with the tool name,
  command, and A/B/C/D approval instructions

#### Scenario: Only requesting user may reply to approval prompt

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** a different Slack user replies with an approval choice
- **THEN** the reply is rejected
- **AND** Slack receives a visible warning that only the requesting user can approve the action

### Requirement: Slack text approval reply routing to session

The Slack channel SHALL route parsed text approval replies back to the
originating session as `ToolInteractionResponse` messages. Routing SHALL use the
pending request state held by the thread binding actor so the reply is matched
to the correct `CallId` and requester.

#### Scenario: User replies Approve Once

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `A`
- **THEN** the Slack channel parses the text reply against the pending approval request
- **AND** sends a `ToolInteractionResponse` with `ApprovedOnce` to the session

#### Scenario: User replies Approve For This Chat

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `B`
- **THEN** a `ToolInteractionResponse` with `ApprovedSession` is sent to the session
- **AND** the approval is retained only for the current Slack thread session

#### Scenario: User replies Approve Always

- **GIVEN** an approval prompt is displayed in a Slack thread
- **WHEN** the user replies `C`
- **THEN** a `ToolInteractionResponse` with `ApprovedAlways` is sent to the session
- **AND** the approval is persisted to `tool-approvals.json`

#### Scenario: User replies Deny

- **GIVEN** an approval prompt is displayed
- **WHEN** the user replies `D`
- **THEN** a `ToolInteractionResponse` with `Denied` is sent to the session
- **AND** the tool receives a denial result

#### Scenario: No pending approval means reply falls through as normal message

- **GIVEN** no approval request is pending for the Slack thread
- **WHEN** a user sends `A`, `B`, `C`, or `D`
- **THEN** the message is not treated as an approval response
