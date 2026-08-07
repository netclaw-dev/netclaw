# netclaw-discord-socket Specification

## Purpose

Netclaw SHALL support Discord as a first-class chat ingress/egress transport with
operational parity to Slack Socket Mode. This capability defines the Discord
gateway adapter — its connection lifecycle and health reporting, fail-closed
configuration validation, normalization of inbound Discord events into
`SendUserMessage` with deterministic thread-aware session identity, ACL-gated
dispatch, reply targeting back to the originating Discord context, text-first
slash command compatibility without requiring Discord app-command registration,
and interactive tool-approval handling that prefers Discord interaction controls
while always supporting a deterministic text fallback.
## Requirements
### Requirement: Discord gateway adapter lifecycle and health

Netclaw SHALL provide a Discord gateway adapter that establishes and maintains a
gateway connection lifecycle equivalent to Slack Socket Mode operationally
(connect, disconnect detection, reconnect attempts, and health reporting).
Adapter startup SHALL fail closed when required Discord security or connection
configuration is invalid.

#### Scenario: Discord adapter reports healthy connection

- **GIVEN** valid Discord adapter configuration is present
- **WHEN** Netclaw starts with Discord enabled
- **THEN** the adapter establishes a gateway connection
- **AND** operator diagnostics report Discord adapter health as connected

#### Scenario: Invalid Discord adapter config fails closed

- **GIVEN** Discord adapter configuration is missing required security-critical fields
- **WHEN** Netclaw starts
- **THEN** startup fails with explicit validation diagnostics
- **AND** Discord ingress does not run in permissive mode

### Requirement: Discord ingress normalization and ACL-gated dispatch

Discord inbound events SHALL be normalized into `SendUserMessage` with complete
source metadata and deterministic session identity. ACL evaluation SHALL run
before session dispatch for all Discord inbound paths.

#### Scenario: Discord inbound message normalized and dispatched

- **GIVEN** a Discord message event from an allowed sender/channel
- **WHEN** the Discord adapter processes the event
- **THEN** it produces `SendUserMessage` with normalized content and metadata
- **AND** it dispatches only after ACL allow decision

#### Scenario: Discord inbound message denied before dispatch

- **GIVEN** a Discord message event from a denied sender/channel
- **WHEN** ACL evaluates the inbound event
- **THEN** the event is denied before session dispatch
- **AND** a structured deny reason is recorded for diagnostics

### Requirement: Discord session identity and reply targeting parity

Discord session identity SHALL be deterministic and thread-aware using
`{channelId}/{threadIdOrMessageId}` where `threadIdOrMessageId` resolves to the
Discord thread ID when present, or the root message ID when not threaded.
Replies SHALL be delivered back to the originating Discord context represented
by that identity.

#### Scenario: Threaded Discord messages route to same session

- **GIVEN** two inbound Discord messages in thread `th-42` under channel `ch-7`
- **WHEN** session keys are derived
- **THEN** both map to `ch-7/th-42`
- **AND** both route to the same session actor

#### Scenario: Non-threaded Discord message uses root message identity

- **GIVEN** an inbound Discord message in channel `ch-7` without thread context
- **WHEN** session key is derived
- **THEN** key is `ch-7/<messageId>`
- **AND** reply delivery targets that originating message context

### Requirement: Text-first slash command compatibility on Discord

Discord adapter behavior SHALL preserve session-level text-first slash command
dispatch for inbound message content beginning with `/` without requiring
Discord app-command registration in MVP.

#### Scenario: Text slash command works without app-command registration

- **GIVEN** Discord app-command registration is not configured
- **WHEN** user sends `/netclaw-operations check health` as a Discord message
- **THEN** slash-command-dispatch processes the message deterministically
- **AND** no Discord platform registration is required for this behavior

### Requirement: Discord interactive approval with deterministic text fallback

The Discord adapter SHALL handle `ToolInteractionRequest` in Discord sessions by
preferring Discord interaction controls when available and SHALL always support
deterministic text fallback with equivalent approval options and outcomes.

#### Scenario: Discord interaction approval path succeeds

- **GIVEN** Discord interaction callbacks are available
- **WHEN** a tool approval request is emitted
- **THEN** the adapter renders interaction controls
- **AND** selected approval decision is routed as `ToolInteractionResponse`

#### Scenario: Interaction path unavailable falls back to text deterministically

- **GIVEN** Discord interaction callbacks are unavailable or fail
- **WHEN** a tool approval request is emitted
- **THEN** the adapter emits a text prompt with deterministic A/B/C/D options
- **AND** text reply parsing routes an equivalent `ToolInteractionResponse`

### Requirement: Agent-initiated proactive Discord channel post

The system SHALL expose a `send_discord_message` LLM tool that posts a new
message to a Discord channel and creates a conversation thread off that message.
The tool SHALL be decorated as a builtin `[NetclawTool]` and discovered through
the channel-tool registration path. The tool SHALL accept a message body, an
optional channel id, and an optional thread name.

When no channel id is provided, the tool SHALL fall back to the configured
default Discord channel. When neither a channel id nor a default channel is
configured, the tool SHALL fail with an actionable error and SHALL NOT post.

The tool SHALL enforce the Discord channel ACL (`DiscordAclPolicy.IsAllowedChannel`,
which permits the default channel or any entry in `AllowedChannelIds`) before
posting. A channel that is not allowed SHALL be rejected with an error and SHALL
NOT be posted to.

The tool SHALL post the message and create a public thread off the posted
message, such that the posted message is the thread root. The new session
identity SHALL be `{parentChannelId}/{threadId}`, consistent with how inbound
thread messages resolve their session identity.

#### Scenario: Proactive post to an allowed channel creates a thread

- **GIVEN** a Discord channel id present in `AllowedChannelIds`
- **WHEN** the agent calls `send_discord_message` with a message body for that
  channel
- **THEN** the message is posted to the channel
- **AND** a thread is created off the posted message
- **AND** the tool returns success identifying the thread session
  `{channelId}/{threadId}`

#### Scenario: Channel id omitted falls back to the default channel

- **GIVEN** a configured default Discord channel
- **WHEN** the agent calls `send_discord_message` without a channel id
- **THEN** the message is posted to the default channel

#### Scenario: No channel id and no default channel is rejected

- **GIVEN** no default Discord channel is configured
- **WHEN** the agent calls `send_discord_message` without a channel id
- **THEN** the tool returns an error
- **AND** no message is posted

#### Scenario: Disallowed channel is rejected

- **GIVEN** a Discord channel id that is neither the default channel nor in
  `AllowedChannelIds`
- **WHEN** the agent calls `send_discord_message` for that channel
- **THEN** the tool returns an error indicating the channel is not allowed
- **AND** no message is posted

#### Scenario: Empty message body is rejected

- **WHEN** the agent calls `send_discord_message` with an empty or
  whitespace-only message body
- **THEN** the tool returns an error
- **AND** no message is posted

### Requirement: Proactive Discord thread is wired into the actor hierarchy

After posting, the tool SHALL wire the created thread into the Discord actor
hierarchy so that user replies in the thread route back to a live session. The
tool SHALL send a `StartProactiveThread` message to the Discord gateway actor
and SHALL await a `ProactiveThreadAck` before reporting success. The gateway
actor SHALL route `StartProactiveThread` to the per-channel conversation actor,
which SHALL create or reuse the per-session binding actor and forward the
message; the binding actor SHALL initialize its session pipeline and reply with
`ProactiveThreadAck`.

The conversation actor SHALL re-check the channel ACL on `StartProactiveThread`
as defense-in-depth and SHALL reject a disallowed channel with a failure
response rather than wiring a session. The conversation actor SHALL also reject
`StartProactiveThread` when the session ingress gate is closed.

If the acknowledgement is not received within the tool's timeout, the tool SHALL
report that the message was posted but the session pipeline did not initialize,
rather than reporting an unqualified success or claiming the post failed.

#### Scenario: Reply to a proactively-created thread routes to its session

- **GIVEN** a thread created by a `send_discord_message` call
- **WHEN** an authorized user replies in that thread
- **THEN** the reply routes to the session bound to `{channelId}/{threadId}`

#### Scenario: Proactive thread wiring is rejected for a disallowed channel

- **GIVEN** a `StartProactiveThread` for a channel not permitted by the channel
  ACL
- **WHEN** the conversation actor receives it
- **THEN** the conversation actor responds with a failure
- **AND** no session binding actor is created for that channel

#### Scenario: Proactive thread wiring is rejected while ingress is closed

- **GIVEN** the session ingress gate is closed (e.g. restart drain active)
- **WHEN** the conversation actor receives a `StartProactiveThread`
- **THEN** the conversation actor responds with a failure

#### Scenario: Session pipeline acknowledges proactive wiring

- **WHEN** a binding actor receives a `StartProactiveThread`
- **THEN** it initializes its session pipeline
- **AND** replies with a `ProactiveThreadAck` carrying the session id

#### Scenario: Post succeeds but acknowledgement times out

- **GIVEN** the message was posted and the thread created
- **WHEN** no `ProactiveThreadAck` is received within the tool timeout
- **THEN** the tool reports the message was posted but the session pipeline did
  not initialize

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

### Requirement: Proactive Discord posting is channel-only in this change

The `send_discord_message` tool SHALL target Discord channels only. It SHALL NOT
accept a user id or open a direct-message channel. This boundary is deliberate:
Discord direct messages are a flat conversation with no distinct thread root, so
the `thread-history-backfill` deferred-hydration amnesia fix cannot apply to a
DM, as already documented by that capability's "Discord DM has no thread root"
limitation. DM proactive posting is deferred to a separately tracked change.

#### Scenario: Tool surface exposes no direct-message target

- **WHEN** the `send_discord_message` tool schema is inspected
- **THEN** it exposes a channel target and message body
- **AND** it exposes no user-id / direct-message parameter

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

