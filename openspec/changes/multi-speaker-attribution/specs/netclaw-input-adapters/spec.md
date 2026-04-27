## ADDED Requirements

### Requirement: Speaker attribution on live message content

The channel pipeline SHALL prepend a speaker attribution tag to
`SendUserMessage.Content` for every live inbound message. The tag format
SHALL be `<speaker: {SenderId}, role={role}>` where `role` is `authorized`
when `ChannelInput.IsObserver` is false and `observer` when
`ChannelInput.IsObserver` is true. The tag SHALL appear on its own line
before the message content.

This ensures the LLM sees consistent speaker identity on both hydrated
history (which uses the same `<speaker:>` format from the merger) and live
ongoing messages.

#### Scenario: Authorized user live message gets speaker tag

- **GIVEN** a `ChannelInput` with `SenderId = "U0123"` and `IsObserver = false`
- **WHEN** `ChannelPipeline.MapToCommand` builds the `SendUserMessage`
- **THEN** `Content` starts with `<speaker: U0123, role=authorized>`
- **AND** the original message text follows on the next line

#### Scenario: Observer live message gets speaker tag

- **GIVEN** a `ChannelInput` with `SenderId = "U9999"` and `IsObserver = true`
- **WHEN** `ChannelPipeline.MapToCommand` builds the `SendUserMessage`
- **THEN** `Content` starts with `<speaker: U9999, role=observer>`
- **AND** the original message text follows on the next line

#### Scenario: Single-speaker session still gets speaker tag

- **GIVEN** a thread with only one participant
- **WHEN** that user sends a message
- **THEN** the `SendUserMessage.Content` includes a `<speaker:>` tag
- **AND** the tag has `role=authorized`

### Requirement: IsObserver flag on ChannelInput

`ChannelInput` SHALL expose a `bool IsObserver` property (default `false`).
The property SHALL be set to `true` when the inbound message originates from
an ACL Observe decision. The property SHALL be propagated from the
conversation actor through the inbound message record
(`SlackThreadInbound.IsObserver`, `DiscordThreadInbound.IsObserver`) to the
binding actor, which sets it on the `ChannelInput`.

#### Scenario: Observer ChannelInput from Slack

- **GIVEN** the Slack ACL returns Observe for user `U999`
- **WHEN** the conversation actor forwards the message
- **THEN** `SlackThreadInbound.IsObserver` is `true`
- **AND** the binding actor builds a `ChannelInput` with `IsObserver = true`

#### Scenario: Non-observer ChannelInput default

- **GIVEN** the ACL returns Allow for user `U111`
- **WHEN** the binding actor builds a `ChannelInput`
- **THEN** `IsObserver` is `false`

### Requirement: Multi-speaker system prompt overlay

When `AllowedUserIds` is non-empty, channel binding actors SHALL set the
`SessionPipelineOptions.PromptOverlay` to a multi-speaker guidance section
that instructs the LLM to only execute instructions from authorized speakers
and treat observer messages as context. The overlay text SHALL be consistent
across Slack and Discord adapters.

When `AllowedUserIds` is empty, no multi-speaker overlay SHALL be set.

#### Scenario: Prompt overlay set when AllowedUserIds is configured

- **GIVEN** `AllowedUserIds` contains `["U111", "U222"]`
- **WHEN** the binding actor builds `SessionPipelineOptions`
- **THEN** `PromptOverlay` contains guidance about authorized vs observer roles
- **AND** the overlay instructs the LLM not to execute observer instructions

#### Scenario: No prompt overlay when AllowedUserIds is empty

- **GIVEN** `AllowedUserIds` is empty
- **WHEN** the binding actor builds `SessionPipelineOptions`
- **THEN** `PromptOverlay` is null

#### Scenario: Overlay is consistent across channels

- **GIVEN** `AllowedUserIds` is non-empty on both Slack and Discord
- **WHEN** both binding actors build `SessionPipelineOptions`
- **THEN** both use identical overlay text (shared constant or utility)

## MODIFIED Requirements

### Requirement: Source metadata on all commands

All inbound `SendUserMessage` commands SHALL carry source metadata sufficient
for ACL evaluation and audit logging. Source metadata SHALL include adapter
type, sender identity, channel identifier, timestamp, and observer status.

#### Scenario: Slack source metadata populated

- **GIVEN** a Slack message event is received
- **WHEN** the Slack adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `slack`
- **AND** includes the Slack user ID as sender identity
- **AND** includes the Slack channel ID
- **AND** includes the event timestamp

#### Scenario: Observer source metadata populated

- **GIVEN** a Slack message from a non-allowed user in an active thread
- **WHEN** the Slack adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes `Principal = UntrustedExternal`
- **AND** `ChannelInput.IsObserver` is `true`

#### Scenario: Timer source metadata populated

- **GIVEN** an Akka timer fires for a scheduled task
- **WHEN** the timer adapter creates the `SendUserMessage` command
- **THEN** the source metadata includes adapter type `timer`
- **AND** includes the task creator as sender identity
- **AND** includes the task ID as the channel equivalent
- **AND** includes the timer fire timestamp
