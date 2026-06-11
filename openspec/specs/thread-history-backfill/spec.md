# thread-history-backfill Specification

## Purpose

Define the channel-agnostic contract for hydrating thread history into the
first inbound event a session sees, and the semantics that any threaded
channel adapter must honour when implementing it. Hydration is an adapter-
internal concern: the session layer is unaware of whether any particular
`SendUserMessage` was enriched with prior-thread context.

## Requirements

### Requirement: Channel-agnostic thread history fetcher

The system SHALL define `IThreadHistoryFetcher` in the channel abstraction
layer. The interface SHALL return `IReadOnlyList<ChannelInput>` in
chronological order for a given `SessionId`. The interface SHALL be pure
retrieval — it SHALL NOT mutate session state, post messages, or update
cursors. Channel adapters that support threaded conversations SHALL provide
an implementation; adapters that do not SHALL NOT.

#### Scenario: Slack adapter provides a fetcher

- **GIVEN** the Slack channel adapter is registered
- **WHEN** `IThreadHistoryFetcher` is resolved from DI
- **THEN** `SlackThreadHistoryFetcher` is returned
- **AND** it uses `ISlackApiClient.Conversations.Replies` internally

#### Scenario: Fetcher is side-effect free

- **GIVEN** a fetcher implementation
- **WHEN** `FetchThreadHistoryAsync` is called
- **THEN** no session state is modified
- **AND** no cursor is advanced
- **AND** no messages are posted to the channel

### Requirement: Hydration merges into the triggering inbound event

Threaded channel adapters SHALL hydrate prior thread messages by merging
them into the triggering inbound event's `ChannelInput` before enqueueing,
not by delivering them as separate messages. The merged `ChannelInput` SHALL
contain a single `TextContent` that frames the historical content with
`[thread history — messages exchanged before this inbound event]` and
`[end thread history]` delimiters, followed by the triggering message's
live text. Image attachments from gap messages SHALL be appended as
`DataContent` items on the merged `ChannelInput`. The session layer SHALL
NOT receive a distinct "backfill" message.

#### Scenario: Merged input appears as a single user turn

- **GIVEN** 3 gap messages and 1 triggering mention
- **WHEN** hydration completes
- **THEN** exactly one `SendUserMessage` reaches the session
- **AND** the LLM sees the thread history and the live mention as a single
  user turn

#### Scenario: Historical sender attribution included

- **GIVEN** a gap message from user `U0123` at `2026-04-09 10:15 UTC`
- **WHEN** the merge runs
- **THEN** the text block contains `<user: U0123, 2026-04-09 10:15 UTC>`
  followed by that message's text

#### Scenario: Image attachments preserved

- **GIVEN** a gap message has one image attachment
- **WHEN** the merge runs
- **THEN** the image bytes appear as a `DataContent` on the merged input
- **AND** the text block records `[image attachments: 1]` for that entry

### Requirement: Multimodal content handling reuses the live pipeline

Image attachments in hydrated messages SHALL be downloaded via the adapter's
existing file-download path with the adapter's authentication credentials
and SHALL be content-scanned through `IContentScanner` before inclusion.
Per-message download or scan failures SHALL be logged and skipped without
aborting the rest of the hydration. Non-image file types SHALL follow the
same filtering rules as live inbound messages.

#### Scenario: Image rejected by content scanner is skipped

- **GIVEN** a gap message image fails content scanning
- **WHEN** the adapter processes that message
- **THEN** the image is excluded from the merged input
- **AND** the text portion of the message is still included
- **AND** the rest of the hydration continues

#### Scenario: Image download failure is skipped

- **GIVEN** a gap message image download fails
- **WHEN** the adapter processes that message
- **THEN** the failed image is skipped with a warning log
- **AND** the text portion of the message is still included

### Requirement: Prompt injection gate on hydrated content

Hydrated message text SHALL be evaluated by `IPromptInjectionDetector`
before being merged. Messages flagged `Risk = High` SHALL be dropped with a
warning log. If the detector fails for a hydrated message, that message
SHALL be dropped and the adapter SHALL surface a warning to the user in
the thread so they know some prior context was excluded. Adapters SHALL
NOT silently pass unchecked content through the hydration path.

#### Scenario: High-risk historical message dropped

- **GIVEN** a gap message triggers the injection detector with `High` risk
- **WHEN** the adapter processes that message
- **THEN** the message is excluded from the merged input
- **AND** a warning is logged with sender and message identifiers

#### Scenario: Detector failure surfaces a user-visible warning

- **GIVEN** the injection detector throws while evaluating a gap message
- **WHEN** the adapter processes that message
- **THEN** the message is excluded from the merged input
- **AND** the adapter posts a user-visible warning in the thread once per
  inbound event

### Requirement: Cursor-based gap computation and stale drop

Threaded channel adapters SHALL maintain a durable per-thread cursor marking
the most recently successfully processed inbound event. Inbound events whose
ordering key (e.g., Slack `ts`) is at or before the cursor SHALL be dropped
as stale with a telemetry counter recording the drop. On the first non-stale
inbound per adapter runtime the adapter SHALL hydrate the gap of messages
whose ordering key is strictly after the cursor and strictly before the
triggering event. The cursor SHALL be advanced only after the triggering
event has been enqueued onto the session input channel.

#### Scenario: Replay after restart is filtered

- **GIVEN** the cursor persisted before restart is `X`
- **WHEN** the adapter restarts and the transport replays an event with
  ordering key `≤ X`
- **THEN** the replayed event is dropped
- **AND** a `stale_event` telemetry counter is incremented

#### Scenario: Gap after restart is hydrated exactly once

- **GIVEN** the persisted cursor is `X` and the first inbound after restart
  has ordering key `Y > X`
- **WHEN** the event is processed
- **THEN** messages with ordering key strictly between `X` and `Y` are
  merged into the triggering event
- **AND** subsequent inbound events in the same runtime do not re-hydrate

#### Scenario: Cursor advances only after successful enqueue

- **GIVEN** hydration and merge succeed but the session input channel write
  fails
- **WHEN** the write failure is handled
- **THEN** the cursor is not advanced
- **AND** the next inbound event will attempt hydration again

### Requirement: No artificial hydration size cap

Adapters SHALL retrieve all messages in the gap without imposing an
artificial message count or token limit. If the merged content exceeds the
session's context window, the existing compaction pipeline SHALL handle
overflow.

#### Scenario: Long thread hydrated in full

- **GIVEN** a thread contains 200 messages with mixed text and images
- **WHEN** the adapter hydrates the gap on a fresh session
- **THEN** all 200 messages are fetched (paginated as needed)
- **AND** all surviving messages are included in the merged content

#### Scenario: Hydration exceeds context window

- **GIVEN** the merged `ChannelInput` exceeds the compaction token limit
- **WHEN** the first LLM turn runs
- **THEN** the compaction pipeline activates
- **AND** the oldest hydrated content is compacted first

### Requirement: Bot message filtering

Adapters SHALL exclude the bot's own messages and any other bot or
integration-webhook messages from hydration to reduce noise and prevent
circular context.

#### Scenario: Bot's own messages excluded

- **GIVEN** a thread contains messages from users and the Netclaw bot
- **WHEN** the adapter hydrates the thread
- **THEN** messages from the Netclaw bot are excluded

#### Scenario: Other bot messages excluded

- **GIVEN** a thread contains messages from a CI bot and a user
- **WHEN** the adapter hydrates the thread
- **THEN** messages carrying a `bot_id` are excluded
- **AND** user messages are included

### Requirement: Bot-authored messages are hydrated from server-side history only at the thread root

Threaded channel adapters SHALL include a bot-authored message from
server-side thread history if and only if that message is the thread
root. A "bot-authored" message is one whose author is identified by
the platform as a bot (Slack: `bot_id` present; Discord: `Author.IsBot`).
The "thread root" is the message whose platform identifier equals the
thread's identity key (Slack: `ts == thread_ts`; Discord:
`MessageId == thread channel id`).

Bot-authored entries below the thread root SHALL be dropped during
history fetch. They are the agent's own (or another session's) prior
in-session outputs, which are already persisted in some session's
transcript via the normal output pipeline. Re-adopting them from
server-side history would surface our own outputs as third-party
adopted context, which is the failure mode this rule prevents
(see issue #955).

The root-only restriction SHALL apply to all bot identities, not only
the local agent's. Channel-level ACL filters (configured per channel)
already determine whether the destination session has permission to
see the thread at all; this requirement does not relax or replace
those filters.

The watermark mechanism defined elsewhere in this capability
("Authorized sync watermark and gap computation") SHALL remain a cost
optimization for repeat fetches; it SHALL NOT be relied upon for
bot-vs-not-bot correctness. The watermark filters by ordering key
(time), not by author, and can lag advancement under crash recovery
or out-of-order delivery — so it cannot be the primitive that
guarantees the agent's own outputs aren't re-adopted.

The fetch SHALL derive a stable sender identifier for each retained
entry. When the platform provides a user id (e.g., Slack's `user`
field on a bot post, Discord's `Author.Id`), the adapter SHALL prefer
it. When only a bot identifier is available (e.g., Slack's `bot_id`
without a `user`), the adapter SHALL use that bot identifier as the
sender id. When neither is available, the entry SHALL be dropped.

The inbound bot-message filter that channel adapters apply to live
inbound events for loop-prevention purposes (e.g., Slack's
`IsBotMessage → drop` at `SlackConversationActor.cs:50`) SHALL remain
unchanged. That filter operates on the live inbound path; this
requirement governs the server-side history-fetch path. The two paths
are independent.

This requirement is inert on channel adapters whose "threads" have no
notion of a distinct thread root — most notably Discord direct
messages, which are a flat conversation in a DM channel. In those
cases, no entry satisfies "MessageId equals thread root id," so no
bot-authored content is hydrated from history. The proactive-post
amnesia scenario is therefore not addressed on Discord DMs; this is a
known limitation of the platform model and is not in scope for this
spec.

#### Scenario: Bot's own posted message at thread root is hydrated as adopted context

- **GIVEN** a channel session that was created by an agent-initiated
  proactive post such that the bot's message is the thread root
- **AND** the producing ephemeral session has terminated and the
  destination session's transcript is empty
- **WHEN** a user replies in the thread, creating an authorized inbound
- **THEN** the history fetcher returns the bot's posted message as an
  entry with the bot's sender id
- **AND** the adopted-context merge layer includes the entry in the
  authorized turn's adopted-context window before the user reply

#### Scenario: Bot reply below the thread root is dropped from history backfill

- **GIVEN** a channel session with a thread that has at least one
  prior agent-authored reply persisted as a turn in the session
  transcript
- **AND** that prior reply also exists in the platform's server-side
  thread history at an ordering key strictly greater than the thread
  root
- **WHEN** the history fetcher hydrates the thread for a subsequent
  authorized inbound
- **THEN** the prior agent-authored reply is NOT included in the
  fetched-history result
- **AND** the adopted-context window built from the fetched history
  does NOT contain the agent's prior reply as a third-party speaker

#### Scenario: Bot-at-root coexists with bot-below-root

- **GIVEN** a proactively-posted thread whose root is bot-authored
- **AND** the thread also contains at least one subsequent
  bot-authored reply (the agent's first in-session turn after the user
  replied)
- **WHEN** the history fetcher hydrates the thread for a later
  authorized inbound
- **THEN** the root bot message IS included in the fetched-history
  result
- **AND** the subsequent bot reply is NOT included

#### Scenario: Human messages are hydrated regardless of position

- **GIVEN** a thread with a human-authored root and multiple
  human-authored replies
- **WHEN** the history fetcher hydrates the thread
- **THEN** all human-authored entries are included irrespective of
  their position relative to the root

#### Scenario: Bot id is the sender fallback when user id is missing

- **GIVEN** a server-side history entry that has a bot id but no user
  id and is at the thread root
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the bot id

#### Scenario: User id is preferred over bot id when both are present

- **GIVEN** a server-side history entry that has both a user id and a
  bot id (common for Slack bot posts authored by a workspace bot user)
  and is at the thread root
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the user id, not the bot id

#### Scenario: Entries with neither user id nor bot id are dropped

- **GIVEN** a server-side history entry that has neither a user id nor a
  bot id (e.g., a system message subtype with no author)
- **WHEN** the history fetcher iterates the entry
- **THEN** the entry is dropped from the hydration result

#### Scenario: Discord DM has no thread root, so no bot content hydrates from history

- **GIVEN** a Discord DM session, where the conversation is flat (no
  distinct thread root) and the session id is keyed on the DM channel
  identifier
- **WHEN** the history fetcher iterates server-side history for the DM
- **THEN** no message satisfies the thread-root predicate
- **AND** no bot-authored entry is hydrated
- **AND** the proactive-post amnesia scenario is not closed on this
  platform shape (known limitation)

### Requirement: Deferred hydration is re-armed until it completes

A threaded channel adapter SHALL re-arm thread-history hydration whenever a
hydration pass fetches a non-empty gap but finds no message in that gap with
`authority-at-inclusion=authorized` to anchor a turn. Such a pass is
**deferred**: the adapter SHALL NOT count its once-per-actor-lifetime hydration
as consumed. A pass that confirmed there is nothing to adopt (empty thread, or
every fetched message at or below the durable watermark) or that enqueued an
authorized turn is instead **completed**.

While hydration is re-armed, the first subsequent **authorized** inbound SHALL
perform the deferred hydration: the adapter SHALL fetch the current thread gap,
classify it, and merge that gap as the adopted-context window preceding the
authorized inbound, which remains the executable message for the turn. The
adapter SHALL enqueue exactly one authorized turn for that inbound and SHALL
then revert to normal fetch-free inbound handling.

Re-arming SHALL be cleared once a re-armed hydration completes (whether or not
the resulting gap was empty). An adapter whose hydration has completed SHALL
NOT fetch thread history again on subsequent inbounds; re-arming therefore
never causes a fetch on an ordinary inbound.

This requirement exists because a proactively-created thread's binding actor
begins its lifetime when the agent posts the thread root — before any
authorized human inbound exists. Its startup hydration necessarily defers,
because the only gap message is the bot-authored root and a bot is not an
allowed user. Without re-arming, the bot root is never adopted and the first
human reply executes with no record of the message that opened the thread.

#### Scenario: Proactively-created thread adopts its bot root on the first authorized reply

- **GIVEN** an agent-initiated proactive post created a thread whose root is the
  bot's own message
- **AND** the binding actor's startup hydration ran while that bot root was the
  only message in the thread and deferred for lack of an authorized trigger
- **WHEN** an authorized user replies in that thread within the same binding
  actor lifetime
- **THEN** the adapter performs the deferred hydration
- **AND** the bot-authored thread root is included in the authorized turn's
  adopted-context window
- **AND** the authorized reply is the executable message for that turn

#### Scenario: Ordinary inbound after a completed hydration does not re-fetch history

- **GIVEN** a binding actor whose hydration completed, either by enqueuing an
  authorized turn or by confirming an empty gap
- **WHEN** a further authorized inbound arrives
- **THEN** the adapter does not fetch thread history for that inbound
- **AND** no adopted-context window is recomputed from server-side history

#### Scenario: Unauthorized inbound while hydration is deferred keeps it re-armed

- **GIVEN** a binding actor whose startup hydration deferred
- **WHEN** a non-allowed user sends a threaded message before any authorized
  inbound arrives
- **THEN** the adapter does not perform the deferred hydration
- **AND** the adapter does not dispatch a turn
- **AND** hydration remains re-armed for the next authorized inbound

#### Scenario: Re-armed hydration fetch failure is non-fatal

- **GIVEN** a binding actor whose startup hydration deferred
- **WHEN** an authorized inbound arrives and the re-armed thread-history fetch
  fails
- **THEN** the authorized inbound is still executed as a turn without an
  adopted-context window
- **AND** hydration remains re-armed so a later authorized inbound can retry

#### Scenario: Discord DM never defers and never re-arms

- **GIVEN** a Discord DM session, whose flat conversation has no distinct
  thread root
- **WHEN** the binding actor's startup hydration runs
- **THEN** no fetched entry satisfies the thread-root predicate, so no
  bot-authored entry is hydrated
- **AND** hydration does not defer on account of a bot root
- **AND** the adapter does not re-arm hydration

### Requirement: Hydration merges into an adopted-context window on authorized inbound

Threaded channel adapters SHALL hydrate prior thread messages only when an
authorized inbound message is about to create an executable turn. The adapter
SHALL merge unsynced prior thread messages into an explicit adopted-context
window before the current authorized message, not as separate turns and not as
ordinary live message history.

The session layer SHALL receive one authorized turn consisting of the adopted
window plus the current authorized message. The session layer SHALL NOT receive
distinct backfill turns for pending speakers.

For adoption semantics, `HasAdoptedContext` SHALL mean exactly that the adopted
window is non-empty. `HasThirdPartyAdoptedContext` SHALL be derived separately
and SHALL be true only when at least one sender id in the adopted window differs
from the current authorized sender for the executable message. Adopted-speaker
provenance SHALL include all sender ids present in the adopted window, including
self-only adopted history.

#### Scenario: Unsynced thread gap adopted only on authorized inbound

- **GIVEN** a thread contains prior unsynced messages
- **WHEN** an authorized user sends the next inbound message
- **THEN** the prior unsynced messages are hydrated into adopted context
- **AND** the current authorized message is appended as the executable message

#### Scenario: Unauthorized inbound does not trigger hydration turn

- **GIVEN** a non-allowed user sends a threaded message
- **WHEN** no authorized user is speaking on that inbound event
- **THEN** the adapter does not dispatch a hydrated turn
- **AND** the message remains pending source-thread context

#### Scenario: Self-only adopted history still counts as adopted context

- **GIVEN** the adopted window contains one or more prior messages from the same
  sender as the current authorized message
- **WHEN** the adapter prepares the authorized turn
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is false

#### Scenario: Third-party speaker sets third-party adopted policy state

- **GIVEN** the adopted window contains messages from `U222`
- **AND** the current authorized sender is `U111`
- **WHEN** the adapter prepares the authorized turn
- **THEN** `HasAdoptedContext` is true
- **AND** `HasThirdPartyAdoptedContext` is true

### Requirement: Authorized sync watermark and gap computation

Threaded channel adapters SHALL maintain a durable per-thread authorized sync
watermark marking the highest thread ordering key whose authorized turn has
completed durably. Adapters MAY also persist a pending cursor for the highest
authorized message accepted for enqueue but not yet durably completed.

For a new authorized inbound with ordering key `Y`, the adapter SHALL hydrate
messages whose ordering key is strictly greater than the watermark and strictly
less than `Y`. The threaded adapter owns source-thread gap fetch and watermark
bookkeeping, while the session owns adopted-context persistence and execution
linkage. When the hydrated gap is non-empty, the adapter SHALL require
adopted-context persistence to succeed before it asks the session to enqueue the
authorized turn. After enqueue acceptance, the adapter SHALL persist or retain a
pending cursor for `Y`. The adapter SHALL advance the durable watermark to `Y`
only after `TurnCompleted` or other durable turn completion for that authorized
message. This sequencing SHALL remain fail-closed for crash recovery: a crash
after enqueue acceptance but before durable completion SHALL NOT promote the
durable watermark. If adopted-context persistence fails, the turn SHALL NOT be
enqueued and neither pending cursor nor durable watermark SHALL advance. If
enqueue is not accepted after persistence succeeds, neither pending cursor nor
durable watermark SHALL advance.

The idempotency basis for adopted-context persistence SHALL be the current
authorized message identity within the session or thread. If the same
authorized message is retried or replayed after a record has already been
persisted, the session SHALL reuse the existing adopted-context record for that
message rather than creating a duplicate.

If no messages exist strictly between the watermark and `Y`, the adapter SHALL
skip adopted-context persistence and adopted-context projection entirely and
enqueue the current authorized message as an ordinary authorized turn.

#### Scenario: First authorized turn adopts full prior gap

- **GIVEN** no watermark exists for a thread
- **AND** an authorized inbound message arrives mid-thread
- **WHEN** hydration runs
- **THEN** all eligible prior thread messages before the current authorized
  message are treated as unsynced and adopted

#### Scenario: Durable watermark advances after durable completion

- **GIVEN** the current watermark is `X`
- **AND** an authorized inbound with ordering key `Y > X` is processed
- **WHEN** that authorized turn later emits `TurnCompleted` with durable
  completion
- **THEN** the durable watermark advances to `Y`

#### Scenario: Pending cursor is recorded after enqueue acceptance

- **GIVEN** the current watermark is `X`
- **AND** an authorized inbound with ordering key `Y > X` is processed
- **WHEN** the resulting authorized turn is accepted for enqueue
- **THEN** the adapter records a pending cursor for `Y`
- **AND** the durable watermark remains `X` until durable completion occurs

#### Scenario: Same authorized message replay reuses adopted-context record

- **GIVEN** authorized inbound `Y` already has a persisted adopted-context
  record for the same session and message identity
- **AND** the watermark has not advanced past `Y`
- **WHEN** that same authorized message is retried or replayed
- **THEN** the existing adopted-context record is reused
- **AND** no duplicate adopted-context record is created

#### Scenario: Watermark does not advance without durable completion

- **GIVEN** the current watermark is `X`
- **AND** hydration for authorized inbound `Y` succeeds
- **WHEN** durable turn completion is never observed for `Y`
- **THEN** the durable watermark remains `X`

#### Scenario: Persistence failure blocks enqueue and watermark advance

- **GIVEN** the current watermark is `X`
- **AND** hydration for authorized inbound `Y` succeeds
- **WHEN** adopted-context persistence fails
- **THEN** the authorized turn is not enqueued
- **AND** neither pending cursor nor durable watermark advances

#### Scenario: Inbound at or before watermark is stale for adoption

- **GIVEN** the current authorized sync watermark is `X`
- **WHEN** a threaded inbound event arrives with ordering key `<= X`
- **THEN** the event is treated as stale for adoption-gap computation
- **AND** no new unsynced adopted window is created from messages at or before
  `X`

### Requirement: Adopted-message inclusion metadata

Each adopted message in the hydrated gap SHALL record message id, sender id,
timestamp, and authority-at-inclusion. Authority-at-inclusion SHALL be captured
at adoption time from the same live turn-creation authorization basis applied
to the inbound event and SHALL be persisted in the adopted-context record.

The adopted-context metadata for the turn SHALL also preserve the complete set of
sender ids present in the adopted window. That provenance SHALL remain inclusive
of any non-empty adopted window and SHALL NOT omit self-only adopted history
merely because no third-party sender is present.

#### Scenario: Unauthorized speaker captured as pending at inclusion time

- **GIVEN** `AllowedUserIds` contains `"U111"`
- **AND** adopted gap history contains a message from `"U999"`
- **WHEN** the adopted-context record is written
- **THEN** that included message records `authority-at-inclusion=pending`

#### Scenario: Authorized historical speaker captured as authorized at inclusion time

- **GIVEN** `AllowedUserIds` contains `"U111"`
- **AND** adopted gap history contains a message from `"U111"`
- **WHEN** the adopted-context record is written
- **THEN** that included message records `authority-at-inclusion=authorized`

#### Scenario: Self-only adopted provenance is preserved

- **GIVEN** the adopted window is non-empty
- **AND** every adopted message sender id matches the current authorized sender
- **WHEN** adopted-context metadata is materialized
- **THEN** the adopted-speaker provenance still includes that sender id
- **AND** the turn still reports adopted context as present

### Requirement: Bot-authored messages are hydrated from server-side history only at the thread root

Threaded channel adapters SHALL include a bot-authored message from
server-side thread history if and only if that message is the thread
root. A "bot-authored" message is one whose author is identified by
the platform as a bot (Slack: `bot_id` present; Discord: `Author.IsBot`).
The "thread root" is the message whose platform identifier equals the
thread's identity key (Slack: `ts == thread_ts`; Discord:
`MessageId == thread channel id`).

Bot-authored entries below the thread root SHALL be dropped during
history fetch. They are the agent's own (or another session's) prior
in-session outputs, which are already persisted in some session's
transcript via the normal output pipeline. Re-adopting them from
server-side history would surface our own outputs as third-party
adopted context, which is the failure mode this rule prevents
(see issue #955).

The root-only restriction SHALL apply to all bot identities, not only
the local agent's. Channel-level ACL filters (configured per channel)
already determine whether the destination session has permission to
see the thread at all; this requirement does not relax or replace
those filters.

The watermark mechanism defined elsewhere in this capability
("Authorized sync watermark and gap computation") SHALL remain a cost
optimization for repeat fetches; it SHALL NOT be relied upon for
bot-vs-not-bot correctness. The watermark filters by ordering key
(time), not by author, and can lag advancement under crash recovery
or out-of-order delivery — so it cannot be the primitive that
guarantees the agent's own outputs aren't re-adopted.

The fetch SHALL derive a stable sender identifier for each retained
entry. When the platform provides a user id (e.g., Slack's `user`
field on a bot post, Discord's `Author.Id`), the adapter SHALL prefer
it. When only a bot identifier is available (e.g., Slack's `bot_id`
without a `user`), the adapter SHALL use that bot identifier as the
sender id. When neither is available, the entry SHALL be dropped.

The inbound bot-message filter that channel adapters apply to live
inbound events for loop-prevention purposes (e.g., Slack's
`IsBotMessage → drop` at `SlackConversationActor.cs:50`) SHALL remain
unchanged. That filter operates on the live inbound path; this
requirement governs the server-side history-fetch path. The two paths
are independent.

This requirement is inert on channel adapters whose "threads" have no
notion of a distinct thread root — most notably Discord direct
messages, which are a flat conversation in a DM channel. In those
cases, no entry satisfies "MessageId equals thread root id," so no
bot-authored content is hydrated from history. The proactive-post
amnesia scenario is therefore not addressed on Discord DMs; this is a
known limitation of the platform model and is not in scope for this
spec.

#### Scenario: Bot's own posted message at thread root is hydrated as adopted context

- **GIVEN** a channel session that was created by an agent-initiated
  proactive post such that the bot's message is the thread root
- **AND** the producing ephemeral session has terminated and the
  destination session's transcript is empty
- **WHEN** a user replies in the thread, creating an authorized inbound
- **THEN** the history fetcher returns the bot's posted message as an
  entry with the bot's sender id
- **AND** the adopted-context merge layer includes the entry in the
  authorized turn's adopted-context window before the user reply

#### Scenario: Bot reply below the thread root is dropped from history backfill

- **GIVEN** a channel session with a thread that has at least one
  prior agent-authored reply persisted as a turn in the session
  transcript
- **AND** that prior reply also exists in the platform's server-side
  thread history at an ordering key strictly greater than the thread
  root
- **WHEN** the history fetcher hydrates the thread for a subsequent
  authorized inbound
- **THEN** the prior agent-authored reply is NOT included in the
  fetched-history result
- **AND** the adopted-context window built from the fetched history
  does NOT contain the agent's prior reply as a third-party speaker

#### Scenario: Bot-at-root coexists with bot-below-root

- **GIVEN** a proactively-posted thread whose root is bot-authored
- **AND** the thread also contains at least one subsequent
  bot-authored reply (the agent's first in-session turn after the user
  replied)
- **WHEN** the history fetcher hydrates the thread for a later
  authorized inbound
- **THEN** the root bot message IS included in the fetched-history
  result
- **AND** the subsequent bot reply is NOT included

#### Scenario: Human messages are hydrated regardless of position

- **GIVEN** a thread with a human-authored root and multiple
  human-authored replies
- **WHEN** the history fetcher hydrates the thread
- **THEN** all human-authored entries are included irrespective of
  their position relative to the root

#### Scenario: Bot id is the sender fallback when user id is missing

- **GIVEN** a server-side history entry that has a bot id but no user
  id and is at the thread root
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the bot id

#### Scenario: User id is preferred over bot id when both are present

- **GIVEN** a server-side history entry that has both a user id and a
  bot id (common for Slack bot posts authored by a workspace bot user)
  and is at the thread root
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the user id, not the bot id

#### Scenario: Entries with neither user id nor bot id are dropped

- **GIVEN** a server-side history entry that has neither a user id nor a
  bot id (e.g., a system message subtype with no author)
- **WHEN** the history fetcher iterates the entry
- **THEN** the entry is dropped from the hydration result

#### Scenario: Discord DM has no thread root, so no bot content hydrates from history

- **GIVEN** a Discord DM session, where the conversation is flat (no
  distinct thread root) and the session id is keyed on the DM channel
  identifier
- **WHEN** the history fetcher iterates server-side history for the DM
- **THEN** no message satisfies the thread-root predicate
- **AND** no bot-authored entry is hydrated
- **AND** the proactive-post amnesia scenario is not closed on this
  platform shape (known limitation)

### Requirement: Deferred hydration is re-armed until it completes

A threaded channel adapter SHALL re-arm thread-history hydration whenever a
hydration pass fetches a non-empty gap but finds no message in that gap with
`authority-at-inclusion=authorized` to anchor a turn. Such a pass is
**deferred**: the adapter SHALL NOT count its once-per-actor-lifetime hydration
as consumed. A pass that confirmed there is nothing to adopt (empty thread, or
every fetched message at or below the durable watermark) or that enqueued an
authorized turn is instead **completed**.

While hydration is re-armed, the first subsequent **authorized** inbound SHALL
perform the deferred hydration: the adapter SHALL fetch the current thread gap,
classify it, and merge that gap as the adopted-context window preceding the
authorized inbound, which remains the executable message for the turn. The
adapter SHALL enqueue exactly one authorized turn for that inbound and SHALL
then revert to normal fetch-free inbound handling.

Re-arming SHALL be cleared once a re-armed hydration completes (whether or not
the resulting gap was empty). An adapter whose hydration has completed SHALL
NOT fetch thread history again on subsequent inbounds; re-arming therefore
never causes a fetch on an ordinary inbound.

This requirement exists because a proactively-created thread's binding actor
begins its lifetime when the agent posts the thread root — before any
authorized human inbound exists. Its startup hydration necessarily defers,
because the only gap message is the bot-authored root and a bot is not an
allowed user. Without re-arming, the bot root is never adopted and the first
human reply executes with no record of the message that opened the thread.

#### Scenario: Proactively-created thread adopts its bot root on the first authorized reply

- **GIVEN** an agent-initiated proactive post created a thread whose root is the
  bot's own message
- **AND** the binding actor's startup hydration ran while that bot root was the
  only message in the thread and deferred for lack of an authorized trigger
- **WHEN** an authorized user replies in that thread within the same binding
  actor lifetime
- **THEN** the adapter performs the deferred hydration
- **AND** the bot-authored thread root is included in the authorized turn's
  adopted-context window
- **AND** the authorized reply is the executable message for that turn

#### Scenario: Ordinary inbound after a completed hydration does not re-fetch history

- **GIVEN** a binding actor whose hydration completed, either by enqueuing an
  authorized turn or by confirming an empty gap
- **WHEN** a further authorized inbound arrives
- **THEN** the adapter does not fetch thread history for that inbound
- **AND** no adopted-context window is recomputed from server-side history

#### Scenario: Unauthorized inbound while hydration is deferred keeps it re-armed

- **GIVEN** a binding actor whose startup hydration deferred
- **WHEN** a non-allowed user sends a threaded message before any authorized
  inbound arrives
- **THEN** the adapter does not perform the deferred hydration
- **AND** the adapter does not dispatch a turn
- **AND** hydration remains re-armed for the next authorized inbound

#### Scenario: Re-armed hydration fetch failure is non-fatal

- **GIVEN** a binding actor whose startup hydration deferred
- **WHEN** an authorized inbound arrives and the re-armed thread-history fetch
  fails
- **THEN** the authorized inbound is still executed as a turn without an
  adopted-context window
- **AND** hydration remains re-armed so a later authorized inbound can retry

#### Scenario: Discord DM never defers and never re-arms

- **GIVEN** a Discord DM session, whose flat conversation has no distinct
  thread root
- **WHEN** the binding actor's startup hydration runs
- **THEN** no fetched entry satisfies the thread-root predicate, so no
  bot-authored entry is hydrated
- **AND** hydration does not defer on account of a bot root
- **AND** the adapter does not re-arm hydration
