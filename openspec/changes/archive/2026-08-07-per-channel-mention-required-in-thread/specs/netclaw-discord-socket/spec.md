## MODIFIED Requirements

### Requirement: Proactive Discord post conforms to thread-history-backfill

A proactively-created Discord thread SHALL satisfy the `thread-history-backfill`
capability's deferred-hydration contract. The proactively-posted bot-authored
message SHALL be the thread root, so that on the first authorized reply the
binding actor's re-armed deferred hydration fetches the thread root from
server-side history and includes it in the authorized turn's adopted-context
window. The tool SHALL NOT seed the session transcript directly; context
recovery SHALL occur through the existing history-backfill path.

As an extension of the deferred-hydration contract, a later inbound message that
mentions the bot SHALL trigger one re-hydration pass when the thread's channel
has the per-channel `MentionRequiredInThread` value on and un-mentioned messages
accumulated since the last completed turn. The re-hydration SHALL be guarded and
SHALL run only when all of the following hold:

1. the triggering message mentions the bot,
2. the persisted thread cursor is strictly before the thread head, so a real gap
   exists, and
3. no turn is in flight for the thread.

The re-hydration SHALL reuse the same server-side history fetch, gap computation,
prompt-injection gate, and adopted-context merge as the deferred hydration. When
any guard condition is not met, the binding actor SHALL NOT re-fetch history.
This preserves the duplicate-content invariant (PR #733), because the thread
cursor advances only after a turn completes.

#### Scenario: Bot root is adopted on the first authorized reply

- **GIVEN** a thread created by `send_discord_message`, whose root is the bot's
  posted message
- **AND** the binding actor's startup hydration deferred because the bot root
  was the only message and a bot is not an authorized user
- **WHEN** an authorized user replies in that thread
- **THEN** the binding actor performs the deferred hydration
- **AND** the bot-authored thread root is included in the authorized turn's
  adopted-context window before the user's reply

#### Scenario: Mention re-hydrates the gap the tap held in a Discord thread

- **GIVEN** a Discord thread whose channel has `MentionRequiredInThread` on
- **AND** un-mentioned replies accumulated since the last completed turn while
  the tap held them back
- **WHEN** an authorized user posts a message that mentions the bot with no turn
  in flight
- **THEN** the binding actor re-runs hydration once
- **AND** the held-back gap is included in the mention turn's adopted-context
  window

#### Scenario: In-flight turn skips the mention re-hydration

- **GIVEN** a Discord thread with `MentionRequiredInThread` on and a turn already
  in flight
- **WHEN** a mention arrives before that turn completes
- **THEN** the binding actor does not re-fetch history
- **AND** the cursor invariant prevents duplicate content

## ADDED Requirements

### Requirement: Per-channel MentionRequiredInThread gate on Discord channel options

`DiscordChannelOptions` SHALL carry a per-channel `MentionRequiredInThread`
value. `DiscordRoutingPolicy` SHALL resolve the value per channel before the
routing decision. A channel with no value SHALL default to `false`, which keeps
today's active-session bypass — an active thread forwards every inbound message
to the session without a mention.

When the value is on for a channel, the adapter SHALL ignore an un-mentioned
message in an active Discord thread: it SHALL NOT forward that message to the
session and SHALL NOT advance the thread cursor. A later message that mentions
the bot SHALL continue the session and SHALL trigger the re-hydration defined by
"Proactive Discord post conforms to thread-history-backfill", so Netclaw catches
up on the messages the tap held.

There SHALL be no connector-wide `MentionRequiredInThread` value. The
connector-wide bool that PR #1783 added SHALL be removed. It was never deployed, so
no config migration is required. The per-channel storage is additive.

The value SHALL gate routing only. It SHALL NOT grant channel access and SHALL
NOT change ACL, audience resolution, or the prompt-injection gate.

#### Scenario: Un-mentioned reply ignored when the gate is on

- **GIVEN** a Discord channel with `MentionRequiredInThread` on and an active
  thread session
- **WHEN** an allowed sender posts a reply in the thread without mentioning the
  bot
- **THEN** the adapter does not forward the message to the session
- **AND** the thread cursor does not advance

#### Scenario: Mention continues the session and re-hydrates

- **GIVEN** a Discord channel with `MentionRequiredInThread` on and un-mentioned
  replies held back since the last completed turn
- **WHEN** an authorized user posts a message that mentions the bot with no turn
  in flight
- **THEN** the adapter forwards the mention to the session
- **AND** the binding actor re-runs hydration to include the held-back gap

#### Scenario: Channel with no value keeps default behavior

- **GIVEN** a Discord channel with no `MentionRequiredInThread` value and an
  active thread session
- **WHEN** an allowed sender posts an un-mentioned reply in the thread
- **THEN** the value resolves to `false`
- **AND** the adapter forwards the reply to the session, as today
