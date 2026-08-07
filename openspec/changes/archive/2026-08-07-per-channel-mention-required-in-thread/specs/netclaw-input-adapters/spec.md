## ADDED Requirements

### Requirement: Per-channel mention requirement gates thread turn creation

Each threaded channel adapter SHALL read a per-channel `MentionRequiredInThread`
value for the channel of an inbound message. The value is per-channel. A channel
with no value SHALL default to `false`, which keeps today's active-session bypass
(a thread with an active session continues without a mention).

When the value is `true` for a channel, an inbound message in an active thread that
does not `@`-mention the bot SHALL NOT create an executable turn. The adapter SHALL
hold it as pending source-thread context — the same disposition an unauthorized
inbound receives under the adopted-context model. An inbound message that
`@`-mentions the bot SHALL create the turn and continue the existing thread session.

The per-channel value SHALL gate turn creation only. It SHALL NOT grant or deny
channel access; the ACL owns access. A bot mention (`AppMention`) SHALL continue to
create a turn regardless of this value.

#### Scenario: Un-mentioned thread reply is held when the value is on

- **GIVEN** a channel with `MentionRequiredInThread = true` and an active thread session
- **WHEN** a user posts a reply in that thread without mentioning the bot
- **THEN** the adapter does not create an executable turn for that reply
- **AND** the reply is held as pending source-thread context

#### Scenario: A mention creates the turn when the value is on

- **GIVEN** a channel with `MentionRequiredInThread = true` and un-mentioned replies held as pending
- **WHEN** a user posts a reply that `@`-mentions the bot
- **THEN** the adapter creates an executable turn and continues the existing thread session

#### Scenario: A channel with no value keeps the active-session bypass

- **GIVEN** a channel with no `MentionRequiredInThread` value configured
- **WHEN** a user posts an un-mentioned reply in a thread with an active session
- **THEN** the adapter creates an executable turn, as it does today

#### Scenario: The value never grants channel access

- **GIVEN** a channel that is not on the adapter's allowed-channels list
- **WHEN** any inbound message arrives for that channel, regardless of `MentionRequiredInThread`
- **THEN** the ACL denies the message
- **AND** the per-channel mention value does not override the access decision
