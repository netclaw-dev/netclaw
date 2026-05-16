## ADDED Requirements

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

#### Scenario: Bot root is adopted on the first authorized reply

- **GIVEN** a thread created by `send_discord_message`, whose root is the bot's
  posted message
- **AND** the binding actor's startup hydration deferred because the bot root
  was the only message and a bot is not an authorized user
- **WHEN** an authorized user replies in that thread
- **THEN** the binding actor performs the deferred hydration
- **AND** the bot-authored thread root is included in the authorized turn's
  adopted-context window before the user's reply

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
