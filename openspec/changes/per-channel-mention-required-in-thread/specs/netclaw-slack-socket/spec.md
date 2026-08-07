## MODIFIED Requirements

### Requirement: Thread hydration on first inbound per runtime

When `SlackThreadBindingActor` is freshly initialized (including after daemon restart), the first non-stale inbound event SHALL trigger a single
thread hydration pass. The actor SHALL call
`IThreadHistoryFetcher.FetchThreadHistoryAsync`, compute the gap of messages
strictly after the cursor and strictly before the triggering event's `ts`,
and merge the surviving gap content into the triggering `ChannelInput`.
Hydration SHALL run at most once per actor runtime; subsequent inbound events
in the same runtime SHALL NOT re-fetch history.

As an exception to the once-per-runtime rule, a later `app_mention` SHALL
trigger one re-hydration pass when the channel's per-channel
`MentionRequiredInThread` value is on and un-mentioned messages accumulated
since the last completed turn. The re-hydration SHALL be guarded and SHALL run
only when all of the following hold:

1. the triggering event is a bot mention,
2. the persisted cursor is strictly before the thread head, so a real gap
   exists, and
3. no turn is in flight for the thread.

The re-hydration SHALL reuse the same fetch, gap computation, prompt-injection
gate, and merge path as the first-inbound hydration. When any guard condition is
not met, the actor SHALL NOT re-fetch history. This preserves the
duplicate-content invariant (PR #733), because the cursor advances only after a
turn completes.

#### Scenario: First inbound after restart hydrates the gap

- **GIVEN** a cursor of `1712700000.000500` persisted from a prior run
- **WHEN** the daemon restarts and a new inbound event with ts
  `1712700000.000900` arrives
- **THEN** the actor fetches full thread history once
- **AND** includes messages with ts strictly between `500` and `900` in the
  merged content
- **AND** sets `_threadHistoryHydrated = true`

#### Scenario: Subsequent ordinary inbound events skip rehydration

- **GIVEN** hydration has already run in this actor runtime
- **AND** the channel's `MentionRequiredInThread` value is off, or the event does not
  satisfy the mention re-hydration guards
- **WHEN** a second inbound event arrives
- **THEN** `IThreadHistoryFetcher` is not invoked
- **AND** the event is enqueued as a normal message with its own content only

#### Scenario: Fresh thread hydration on first mention

- **GIVEN** no cursor has ever been persisted for this thread
- **WHEN** an `app_mention` inbound event arrives
- **THEN** the actor fetches the full thread history
- **AND** includes all messages with ts strictly before the mention event

#### Scenario: Mention re-hydrates the gap the tap held

- **GIVEN** the channel's `MentionRequiredInThread` value is on
- **AND** the cursor is `1712700000.000500` and un-mentioned messages posted
  after it were ignored since the last completed turn
- **WHEN** an `app_mention` event with ts `1712700000.000900` arrives with no
  turn in flight
- **THEN** the actor re-runs hydration once
- **AND** includes messages with ts strictly between `500` and `900` in the
  merged content

#### Scenario: In-flight turn skips the mention re-hydration

- **GIVEN** the channel's `MentionRequiredInThread` value is on and a turn is
  already in flight for the thread
- **WHEN** an `app_mention` event arrives before that turn completes
- **THEN** the actor does not re-fetch history
- **AND** the cursor invariant prevents duplicate content

## ADDED Requirements

### Requirement: Per-channel MentionRequiredInThread gate on Slack channel options

`SlackChannelOptions` SHALL carry a per-channel `MentionRequiredInThread` value.
`SlackRoutingPolicy` SHALL resolve the value per channel before the routing
decision. A channel with no value SHALL default to `false`, which keeps today's
active-session bypass — an active thread forwards every inbound message to the
session without a mention.

When the value is on for a channel, the adapter SHALL ignore an un-mentioned
message in an active thread: it SHALL NOT forward that message to the session and
SHALL NOT advance the thread cursor. A later `app_mention` SHALL continue the
session and SHALL trigger the re-hydration defined by "Thread hydration on first
inbound per runtime", so Netclaw catches up on the messages the tap held.

There SHALL be no connector-wide `MentionRequiredInThread` value. The
connector-wide bool that PR #1783 added SHALL be removed. It was never deployed, so
no config migration is required. The per-channel storage is additive.

The value SHALL gate routing only. It SHALL NOT grant channel access and SHALL
NOT change ACL, audience resolution, or the prompt-injection gate.

#### Scenario: Un-mentioned reply ignored when the gate is on

- **GIVEN** a Slack channel with `MentionRequiredInThread` on and an active
  thread session
- **WHEN** an allowed sender posts a reply in the thread without mentioning the
  bot
- **THEN** the adapter does not forward the message to the session
- **AND** the thread cursor does not advance

#### Scenario: Mention continues the session and re-hydrates

- **GIVEN** a Slack channel with `MentionRequiredInThread` on and un-mentioned
  replies held back since the last completed turn
- **WHEN** a user posts an `app_mention` in the thread with no turn in flight
- **THEN** the adapter forwards the mention to the session
- **AND** the binding actor re-runs hydration to include the held-back gap

#### Scenario: Channel with no value keeps default behavior

- **GIVEN** a Slack channel with no `MentionRequiredInThread` value and an active
  thread session
- **WHEN** an allowed sender posts an un-mentioned reply in the thread
- **THEN** the value resolves to `false`
- **AND** the adapter forwards the reply to the session, as today
