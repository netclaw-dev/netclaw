## ADDED Requirements

### Requirement: Inbound channel ACL denies by default

The Slack, Discord, and Mattermost adapters SHALL evaluate a channel ACL before
they dispatch an inbound message to a session:

- A message without a sender identifier SHALL be denied.
- A direct message SHALL be denied when the channel does not allow direct
  messages.
- A channel message SHALL be denied unless it comes from the configured default
  channel or a channel in `AllowedChannelIds`.
- When `AllowedUserIds` is not empty, a sender outside that list SHALL be
  denied. An empty `AllowedUserIds` list SHALL allow every sender in an allowed
  channel.
- An audience resolution error SHALL deny the message.

This ACL decides who can talk to Netclaw. It grants no tool authority; tool
authority comes from the audience (`tool-authorization` TA-1 and TA-3).

#### Scenario: Message from a channel that is not allowed

- **GIVEN** a Slack channel that is neither the default channel nor in `AllowedChannelIds`
- **WHEN** a user posts a message in that channel
- **THEN** the adapter denies the message with reason `channel_not_allowed`
- **AND** no session receives it

#### Scenario: Sender outside a non-empty user allow list

- **GIVEN** a Discord channel with `AllowedUserIds` that does not contain user `u2`
- **WHEN** `u2` posts in an allowed channel
- **THEN** the adapter denies the message with reason `user_not_allowed`

#### Scenario: Empty user allow list admits senders in an allowed channel

- **GIVEN** a Mattermost channel with an empty `AllowedUserIds` list
- **WHEN** a user posts in the default channel
- **THEN** the ACL allows the message
