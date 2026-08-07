## MODIFIED Requirements

### Requirement: Mattermost thread-history backfill

The Mattermost channel SHALL provide thread-history backfill via
`IThreadHistoryFetcher`. Backfill SHALL hydrate bot-authored messages only when
the message is the thread root post; bot-authored messages below the root SHALL
be excluded so the agent never re-adopts its own prior output as external
context. A watermark cursor MAY be used as a cost optimization but SHALL NOT be
the deduplication primitive. Deferred one-shot hydration SHALL re-arm and
complete on the first authorized inbound message. History-fetched messages SHALL
carry the channel's resolved trust audience.

As an extension of the deferred one-shot hydration, a later inbound post that
mentions the bot SHALL trigger one re-hydration pass when the thread's channel
has the per-channel `MentionRequiredInThread` value on and un-mentioned posts
accumulated since the last completed turn. The re-hydration SHALL be guarded and
SHALL run only when all of the following hold:

1. the triggering post mentions the bot,
2. the persisted watermark cursor is strictly before the thread head, so a real
   gap exists, and
3. no turn is in flight for the thread.

The re-hydration SHALL reuse the same fetcher, root-post filtering,
prompt-injection gate, and audience resolution as the deferred hydration. When
any guard condition is not met, the channel SHALL NOT re-fetch history. This
preserves the duplicate-content invariant (PR #733), because the cursor advances
only after a turn completes.

#### Scenario: Bot output below the thread root is not adopted

- **GIVEN** a Mattermost thread containing a bot-authored root post and bot-authored reply posts below it
- **WHEN** thread-history backfill runs
- **THEN** the bot-authored root post is hydrated
- **AND** the bot-authored reply posts below the root are excluded

#### Scenario: Deferred hydration completes on first authorized inbound

- **GIVEN** a Mattermost thread created by a proactive send, with hydration deferred because no authorized inbound has arrived
- **WHEN** the first authorized inbound message arrives in that thread
- **THEN** deferred hydration re-arms and completes
- **AND** the proactive thread root is adopted into context

#### Scenario: History-fetched message carries the resolved audience

- **GIVEN** a Mattermost direct message configured with a `dm` channel audience override
- **WHEN** the thread-history fetcher converts a historical post into a `ChannelInput`
- **THEN** the `ChannelInput` carries the audience resolved by the channel's audience policy

#### Scenario: Mention re-hydrates the gap the tap held in a Mattermost thread

- **GIVEN** a Mattermost thread with root post `p-root` whose channel has
  `MentionRequiredInThread` on
- **AND** un-mentioned posts accumulated since the last completed turn while the
  tap held them back
- **WHEN** an authorized user posts a reply that mentions the bot with no turn in
  flight
- **THEN** the channel re-runs thread-history backfill once
- **AND** the held-back gap is adopted into context before the mention turn

#### Scenario: In-flight turn skips the mention re-hydration

- **GIVEN** a Mattermost thread with `MentionRequiredInThread` on and a turn
  already in flight
- **WHEN** a mentioning post arrives before that turn completes
- **THEN** the channel does not re-fetch history
- **AND** the cursor invariant prevents duplicate content

## ADDED Requirements

### Requirement: Per-channel MentionRequiredInThread gate on Mattermost channel options

`MattermostChannelOptions` SHALL carry a per-channel `MentionRequiredInThread`
value. `MattermostRoutingPolicy` SHALL resolve the value per channel before the
routing decision. A channel with no value SHALL default to `false`, which keeps
today's active-session bypass — an active thread forwards every inbound post to
the session without a mention.

When the value is on for a channel, the adapter SHALL ignore an un-mentioned post
in an active thread: it SHALL NOT forward that post to the session and SHALL NOT
advance the thread cursor. A later post that mentions the bot SHALL continue the
session and SHALL trigger the re-hydration defined by "Mattermost thread-history
backfill", so Netclaw catches up on the posts the tap held.

There SHALL be no connector-wide `MentionRequiredInThread` value. The
connector-wide bool that PR #1783 added SHALL be removed. It was never deployed, so
no config migration is required. The per-channel storage is additive.

The value SHALL gate routing only. It SHALL NOT grant channel access and SHALL
NOT change ACL, audience resolution, or the prompt-injection gate.

#### Scenario: Un-mentioned reply ignored when the gate is on

- **GIVEN** a Mattermost channel with `MentionRequiredInThread` on and an active
  thread session
- **WHEN** an allowed sender posts a reply in the thread without mentioning the
  bot
- **THEN** the adapter does not forward the post to the session
- **AND** the thread cursor does not advance

#### Scenario: Mention continues the session and re-hydrates

- **GIVEN** a Mattermost channel with `MentionRequiredInThread` on and
  un-mentioned posts held back since the last completed turn
- **WHEN** an authorized user posts a reply that mentions the bot with no turn in
  flight
- **THEN** the adapter forwards the mentioning post to the session
- **AND** the channel re-runs thread-history backfill to include the held-back
  gap

#### Scenario: Channel with no value keeps default behavior

- **GIVEN** a Mattermost channel with no `MentionRequiredInThread` value and an
  active thread session
- **WHEN** an allowed sender posts an un-mentioned reply in the thread
- **THEN** the value resolves to `false`
- **AND** the adapter forwards the post to the session, as today
