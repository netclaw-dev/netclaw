## ADDED Requirements

### Requirement: Bot-authored messages are hydrated from server-side history

Threaded channel adapters SHALL include bot-authored messages — including
messages authored by the local agent's own bot identity — when hydrating
prior thread history from the platform's server-side history API. Adapters
SHALL NOT unconditionally drop bot messages during history fetch.

The fetch SHALL derive a stable sender identifier for each bot-authored
entry. When the platform provides a user id for the bot (e.g., Slack's
`user` field on bot posts), the adapter SHALL prefer it. When only a bot
identifier is available (e.g., Slack's `bot_id` without a `user`), the
adapter SHALL use that bot identifier as the sender id. When neither is
available, the entry SHALL be dropped.

The watermark mechanism defined elsewhere in this capability ("Authorized
sync watermark and gap computation") SHALL remain the dedup primitive:
bot-authored entries already processed by an in-session turn are above the
session's durable watermark and therefore are not refetched. The only
bot-authored history entries that surface through this requirement are
entries that were never captured by an in-session turn — in practice, the
opening message of a proactively-posted thread whose producing session
terminated without recording the message in the destination session's
transcript.

The inbound bot-message filter that channel adapters apply to live
inbound events for loop-prevention purposes (e.g., Slack's
`IsBotMessage → drop` at `SlackConversationActor.cs:50`) SHALL remain
unchanged. That filter operates on the live inbound path; this
requirement governs the server-side history-fetch path. The two paths
are independent.

#### Scenario: Bot's own posted message at thread root is hydrated as adopted context

- **GIVEN** a channel session that was created by an agent-initiated
  proactive post such that the bot's message is the thread root
- **AND** the producing ephemeral session has terminated and the
  destination session's transcript is empty
- **WHEN** a user replies in the thread, creating an authorized inbound
  with the watermark at zero
- **THEN** the history fetcher returns the bot's posted message as an
  entry with the bot's sender id
- **AND** the adopted-context merge layer includes the entry in the
  authorized turn's adopted-context window before the user reply

#### Scenario: Bot messages above the watermark are not refetched

- **GIVEN** a channel session has already processed an in-session turn
  that produced a bot reply at thread ordering key K
- **AND** the durable watermark has advanced to K
- **WHEN** a subsequent authorized inbound arrives at ordering key K+1
- **THEN** the history fetcher does not include the prior bot reply at
  K in the gap fetch result, because K is at the watermark, not strictly
  above it

#### Scenario: Bot id is the sender fallback when user id is missing

- **GIVEN** a server-side history entry that has a bot id but no user id
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the bot id

#### Scenario: User id is preferred over bot id when both are present

- **GIVEN** a server-side history entry that has both a user id and a
  bot id (common for Slack bot posts authored by a workspace bot user)
- **WHEN** the history fetcher converts the entry to a `ChannelInput`
- **THEN** the resulting input's `SenderId` is the user id, not the bot id

#### Scenario: Entries with neither user id nor bot id are dropped

- **GIVEN** a server-side history entry that has neither a user id nor a
  bot id (e.g., a system message subtype with no author)
- **WHEN** the history fetcher iterates the entry
- **THEN** the entry is dropped from the hydration result
