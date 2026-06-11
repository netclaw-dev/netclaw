## MODIFIED Requirements

### Requirement: Mattermost proactive sends

The Mattermost channel SHALL support proactive sends through the generic
`send_channel_message` tool with `channel_key = "mattermost"`, dispatched
through the Mattermost `IChannelOutboundClient`. The per-channel
`send_mattermost_message` tool is removed.

Proactive sends that initialize a new thread SHALL complete an acknowledged
handshake so the caller knows thread initialization succeeded before
continuing. Proactive sends to direct messages SHALL be permitted only when
direct messages are enabled in channel configuration, and the target user
SHALL be validated against the configured user ACL; the ephemeral DM channel
id opened for delivery SHALL NOT be required in the channel allowlist.

#### Scenario: Proactive send initializes a thread with acknowledgement

- **GIVEN** the agent invokes `send_channel_message` with
  `channel_key = "mattermost"` targeting a new thread
- **WHEN** the post is created
- **THEN** the channel completes a thread-initialization acknowledgement
- **AND** the caller observes success before continuing

#### Scenario: Proactive direct message blocked when DMs are disabled

- **GIVEN** Mattermost channel configuration has direct messages disabled
- **WHEN** the agent invokes `send_channel_message` targeting a direct message
- **THEN** the send is rejected with an explicit reason

#### Scenario: Proactive direct message session wiring uses the user ACL

- **GIVEN** Mattermost direct messages are enabled and a user id passes the
  user ACL
- **WHEN** the agent invokes `send_channel_message` with
  `destination.kind = "direct_message"` for that user
- **THEN** the message is delivered and the DM session pipeline initializes
  successfully even though the ephemeral DM channel id is not in the channel
  allowlist
