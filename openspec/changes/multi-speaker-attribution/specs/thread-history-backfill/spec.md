## MODIFIED Requirements

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

#### Scenario: Historical sender attribution with role

- **GIVEN** a gap message from user `U0123` at `2026-04-09 10:15 UTC`
- **AND** `AllowedUserIds` contains `["U0123"]`
- **WHEN** the merge runs
- **THEN** the text block contains
  `<speaker: U0123, role=authorized, 2026-04-09 10:15 UTC>`
  followed by that message's text

#### Scenario: Historical observer attribution

- **GIVEN** a gap message from user `U9999` at `2026-04-09 10:20 UTC`
- **AND** `AllowedUserIds` contains `["U0123"]` (U9999 is not listed)
- **WHEN** the merge runs
- **THEN** the text block contains
  `<speaker: U9999, role=observer, 2026-04-09 10:20 UTC>`
  followed by that message's text

#### Scenario: No AllowedUserIds configured — all speakers authorized

- **GIVEN** `AllowedUserIds` is empty
- **AND** gap messages exist from users `U111` and `U222`
- **WHEN** the merge runs
- **THEN** both messages are tagged with `role=authorized`

#### Scenario: Image attachments preserved

- **GIVEN** a gap message has one image attachment
- **WHEN** the merge runs
- **THEN** the image bytes appear as a `DataContent` on the merged input
- **AND** the text block records `[image attachments: 1]` for that entry

## ADDED Requirements

### Requirement: Merger accepts allow-list for role classification

`ThreadHistoryContentMerger.MergeHistoryWithLiveContents` SHALL accept an
optional `IReadOnlySet<string>? allowedUserIds` parameter. When provided,
each historical message's `SenderId` SHALL be checked against the set to
determine the speaker role. Senders in the set SHALL be tagged
`role=authorized`. Senders not in the set SHALL be tagged `role=observer`.
When the parameter is null (no allow-list configured), all speakers SHALL
be tagged `role=authorized`.

The binding actors SHALL pass the channel options' `AllowedUserIds` as a
`HashSet<string>` when invoking the merger. When `AllowedUserIds` is empty,
they SHALL pass null.

#### Scenario: Merger with allow-list classifies roles

- **GIVEN** `allowedUserIds = {"U111"}`
- **AND** history contains messages from `U111` and `U222`
- **WHEN** `MergeHistoryWithLiveContents` runs
- **THEN** `U111`'s message is tagged `role=authorized`
- **AND** `U222`'s message is tagged `role=observer`

#### Scenario: Merger without allow-list defaults to authorized

- **GIVEN** `allowedUserIds = null`
- **AND** history contains messages from `U111` and `U222`
- **WHEN** `MergeHistoryWithLiveContents` runs
- **THEN** both messages are tagged `role=authorized`
