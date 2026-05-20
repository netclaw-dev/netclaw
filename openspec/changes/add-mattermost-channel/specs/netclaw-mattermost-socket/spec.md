## ADDED Requirements

### Requirement: Mattermost gateway adapter lifecycle and health

Netclaw SHALL provide a Mattermost channel adapter that establishes and
maintains a Mattermost WebSocket connection lifecycle equivalent to Slack Socket
Mode operationally (connect, disconnect detection, bounded-backoff reconnect,
and health reporting). The adapter SHALL classify connection failures as Fatal
or Transient. Token validity SHALL be checked during channel start, never during
dependency-injection registration, so that a misconfigured Mattermost channel
cannot abort daemon construction or other channels.

#### Scenario: Mattermost adapter reports healthy connection

- **GIVEN** valid Mattermost server URL and bot token configuration is present
- **WHEN** Netclaw starts with the Mattermost channel enabled
- **THEN** the adapter establishes a WebSocket connection
- **AND** operator diagnostics report Mattermost channel health as connected

#### Scenario: Missing or invalid token degrades the channel in isolation

- **GIVEN** the Mattermost channel is enabled but the bot token is missing or invalid
- **WHEN** Netclaw starts
- **THEN** dependency-injection registration does not throw
- **AND** the channel reports degraded health with an explicit diagnostic
- **AND** the daemon and all other channels continue running

#### Scenario: Fatal close code stops reconnect attempts

- **GIVEN** an active Mattermost WebSocket connection
- **WHEN** the connection closes with a Fatal-classified failure (such as an authentication rejection)
- **THEN** the adapter stops the WebSocket client to prevent reconnect spam
- **AND** reports disconnected health

#### Scenario: Transient disconnect triggers bounded-backoff reconnect

- **GIVEN** an active Mattermost WebSocket connection
- **WHEN** the connection closes with a Transient-classified failure
- **THEN** the adapter retries the connection on a bounded backoff schedule
- **AND** session identity continuity is preserved across reconnect

### Requirement: Mattermost ingress normalization and ACL-gated dispatch

Mattermost inbound post events SHALL be normalized into a `ChannelInput` with
complete, explicit trust context and deterministic session identity. ACL
evaluation SHALL run before session dispatch for all Mattermost inbound paths,
including direct messages. Duplicate post events SHALL be suppressed before
dispatch.

#### Scenario: Mattermost inbound post normalized and dispatched

- **GIVEN** a Mattermost post event from an allowed sender in an allowed channel
- **WHEN** the Mattermost adapter processes the event
- **THEN** it produces a `ChannelInput` with normalized content and complete trust context
- **AND** it dispatches only after an ACL allow decision

#### Scenario: Mattermost inbound post denied before dispatch

- **GIVEN** a Mattermost post event from a denied sender or channel
- **WHEN** ACL evaluates the inbound event
- **THEN** the event is denied before session dispatch
- **AND** a structured deny reason is recorded for diagnostics

#### Scenario: Duplicate Mattermost post event suppressed

- **GIVEN** the gateway has already processed a post with a given post ID
- **WHEN** the same post event is delivered again
- **THEN** the gateway suppresses the duplicate
- **AND** no second session dispatch occurs

#### Scenario: The channel's own bot posts do not start sessions

- **GIVEN** the Mattermost adapter receives a post event authored by its own bot user
- **WHEN** the gateway processes the event
- **THEN** no inbound session dispatch occurs for that post

### Requirement: Mattermost session identity and reply targeting parity

Mattermost session identity SHALL be deterministic and thread-aware using the
entity-key pattern `{channelId}/{rootPostId}`, where `rootPostId` resolves to
the Mattermost thread root post ID when the post is a thread reply, or the
post's own ID when it is a root post. Direct messages SHALL use the
direct-message channel ID as `channelId`. Replies SHALL be delivered into the
originating Mattermost thread.

#### Scenario: Thread replies route to the same session

- **GIVEN** two inbound Mattermost posts with root post `p-root` in channel `ch-1`
- **WHEN** session keys are derived
- **THEN** both map to `ch-1/p-root`
- **AND** both route to the same session actor

#### Scenario: Root post uses its own identity

- **GIVEN** an inbound Mattermost root post `p-9` in channel `ch-1` with no thread root
- **WHEN** the session key is derived
- **THEN** the key is `ch-1/p-9`
- **AND** reply delivery targets thread `p-9`

#### Scenario: Direct message uses the DM channel identity

- **GIVEN** an inbound Mattermost post in a direct-message channel `dm-ch-5`
- **WHEN** the session key is derived
- **THEN** `channelId` is `dm-ch-5`
- **AND** replies are delivered into that direct-message channel

#### Scenario: Replies are posted into the originating thread

- **GIVEN** an allowed sender posts in thread `p-root`
- **WHEN** the turn completes
- **THEN** Netclaw posts the reply into thread `p-root`

### Requirement: Mattermost thread-history backfill

The Mattermost channel SHALL provide thread-history backfill via
`IThreadHistoryFetcher`. Backfill SHALL hydrate bot-authored messages only when
the message is the thread root post; bot-authored messages below the root SHALL
be excluded so the agent never re-adopts its own prior output as external
context. A watermark cursor MAY be used as a cost optimization but SHALL NOT be
the deduplication primitive. Deferred one-shot hydration SHALL re-arm and
complete on the first authorized inbound message. History-fetched messages SHALL
carry the channel's resolved trust audience.

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

### Requirement: Mattermost proactive sends

The Mattermost channel SHALL expose a `send_mattermost_message` tool that posts
a message into a Mattermost channel or thread. Proactive sends that initialize a
new thread SHALL complete an acknowledged handshake so the caller knows thread
initialization succeeded before continuing. Proactive sends to direct messages
SHALL be permitted only when direct messages are enabled in channel
configuration.

#### Scenario: Proactive send initializes a thread with acknowledgement

- **GIVEN** the agent invokes `send_mattermost_message` targeting a new thread
- **WHEN** the post is created
- **THEN** the channel completes a thread-initialization acknowledgement
- **AND** the caller observes success before continuing

#### Scenario: Proactive direct message blocked when DMs are disabled

- **GIVEN** Mattermost channel configuration has direct messages disabled
- **WHEN** the agent invokes `send_mattermost_message` targeting a direct message
- **THEN** the send is rejected with an explicit reason

### Requirement: Mattermost scheduled-reminder delivery

The Mattermost channel SHALL provide an `IReminderTargetResolver` that
canonicalizes Mattermost reminder targets before persistence. Channel targets
(`channel:<channelId>`) and direct-message targets (`@<userId>`) SHALL both be
supported, because Mattermost direct messages are addressable channels with
stable IDs. The canonical form SHALL retain the `channel:` or `@` prefix —
Mattermost channel IDs and user IDs are both 26-character alphanumeric strings,
so the prefix is the only signal that lets downstream dispatch distinguish a
channel post from a DM open. Ambiguous bare identifiers SHALL be rejected.
`Channel`-delivery reminders SHALL post via the channel's canonical
notification path; `CurrentSession`-delivery reminders SHALL re-enter the
originating session.

#### Scenario: Channel reminder target canonicalized and delivered

- **GIVEN** a reminder configured with a `channel:<channelId>` Mattermost target
- **WHEN** the reminder fires in `Channel` delivery mode
- **THEN** the target is resolved to a canonical `channel:<channelId>` form
- **AND** the reminder message is posted to that channel

#### Scenario: Direct-message reminder target is supported

- **GIVEN** a reminder configured with an `@<userId>` Mattermost target
- **WHEN** the reminder target is resolved
- **THEN** it resolves to the canonical `@<userId>` form
- **AND** the reminder is accepted as a valid target

#### Scenario: Ambiguous bare identifier rejected

- **GIVEN** a reminder configured with a bare identifier that could be a user or a channel
- **WHEN** the Mattermost reminder target resolver evaluates it
- **THEN** the target is rejected with an explicit disambiguation error

#### Scenario: Duplicate reminder fire does not execute twice

- **GIVEN** a Mattermost reminder execution is already in flight
- **WHEN** the same reminder fires again
- **THEN** the second fire is acknowledged and dropped
- **AND** no parallel execution occurs

### Requirement: Mattermost reminder-spawned interactive sessions

`Channel`-delivery reminders SHALL spawn a fresh interactive session that an
operator can continue in the delivered Mattermost thread. The spawned session
SHALL use a deterministic schedule-scoped entity key and SHALL apply the
reminder's stored audience and tool grants.

#### Scenario: Channel reminder spawns a continuable session

- **GIVEN** a reminder fires in `Channel` delivery mode targeting Mattermost channel `ch-1`
- **WHEN** the reminder message is posted
- **THEN** a fresh session is created with a schedule-scoped entity key
- **AND** a subsequent reply in that Mattermost thread routes into the same session

#### Scenario: Spawned session uses the reminder's stored audience

- **GIVEN** a reminder was minted with a specific audience
- **WHEN** the reminder execution session is created
- **THEN** the session applies the stored audience, not a live channel default

### Requirement: Mattermost interactive approval with deterministic text fallback

The Mattermost channel SHALL declare interactive approval support. It SHALL
render `ToolInteractionRequest` outputs as Mattermost interactive message
buttons when interactive approvals are configured, and SHALL always support a
deterministic text-reply fallback (A/B/C/D) with equivalent approval options and
outcomes. Pending-approval state SHALL be held by the session actor and
approval responses SHALL be routed by session identity, so that a passivated and
re-spawned binding actor does not drop an approval response.

#### Scenario: Mattermost renders interactive approval buttons

- **GIVEN** the Mattermost channel has interactive approvals configured
- **WHEN** the agent invokes an unapproved tool that requires approval
- **THEN** the channel renders the approval prompt as Mattermost interactive buttons

#### Scenario: Mattermost falls back to deterministic text options

- **GIVEN** the Mattermost channel does not have interactive approvals configured
- **WHEN** the agent invokes an unapproved tool that requires approval
- **THEN** the channel renders a deterministic A/B/C/D text approval prompt
- **AND** text replies map to equivalent approval decisions

#### Scenario: Approval response survives binding passivation

- **GIVEN** a pending Mattermost approval whose binding actor has been passivated
- **WHEN** the approval response arrives
- **THEN** it is routed by session identity to the owning session actor
- **AND** the approval decision is applied without being dropped
