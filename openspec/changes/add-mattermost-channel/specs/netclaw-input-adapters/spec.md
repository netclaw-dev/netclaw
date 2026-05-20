## MODIFIED Requirements

### Requirement: Channel interactive approval capability

Each channel implementation SHALL declare whether it supports interactive
approval via a capability flag (`SupportsInteractiveApproval`). The capability
SHALL be queryable from `ToolExecutionContext` or `MessageSource` at tool
invocation time. Channels that support interactive approval MUST be able to
render `ToolInteractionRequest` outputs and route `ToolInteractionResponse`
messages back to the session actor.

#### Scenario: Slack channel declares approval support

- **GIVEN** the Slack channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `true`

#### Scenario: Mattermost channel declares approval support

- **GIVEN** the Mattermost channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `true`

#### Scenario: Headless channel declares no approval support

- **GIVEN** the headless (single-prompt CLI) channel is active
- **WHEN** the system queries channel capabilities
- **THEN** `SupportsInteractiveApproval` is `false`

#### Scenario: Capability flows to tool execution context

- **GIVEN** a session on the Slack channel
- **WHEN** a tool execution context is created
- **THEN** the context includes the channel's `SupportsInteractiveApproval`
  value
- **AND** `ToolAccessPolicy` can use it to determine approval behavior

### Requirement: Inbound adapters supply explicit trust context

Every inbound channel adapter SHALL stamp complete, explicit trust context —
audience, principal, boundary, and provenance — onto each `ChannelInput` it
constructs. The session pipeline SHALL NOT synthesize a default audience,
principal, boundary, or provenance for an inbound message. The
`ChannelInput`-to-`MessageSource` factory SHALL carry trust context through by
direct assignment, with no null-coalescing fallback.

#### Scenario: Adapter omitting trust context fails to compile

- **WHEN** an inbound adapter constructs a `ChannelInput` without every trust
  field set
- **THEN** the build fails with a missing-required-member error

#### Scenario: History-fetched messages carry the resolved audience

- **GIVEN** a Slack DM configured with `Slack.ChannelAudiences["dm"] = "personal"`
- **WHEN** the thread-history fetcher converts a historical message into a
  `ChannelInput`
- **THEN** the `ChannelInput` carries `TrustAudience.Personal` as resolved by
  the channel's audience policy
- **AND** the pipeline applies Personal-level grants without any Public
  fallback

#### Scenario: Mattermost history-fetched messages carry the resolved audience

- **GIVEN** a Mattermost direct message configured with a `dm` channel audience
  override
- **WHEN** the Mattermost thread-history fetcher converts a historical post into
  a `ChannelInput`
- **THEN** the `ChannelInput` carries the audience resolved by the Mattermost
  channel's audience policy
- **AND** no value originates from a pipeline-level default

#### Scenario: Pipeline does not synthesize trust context

- **WHEN** the message-source factory builds a `MessageSource` from a
  `ChannelInput`
- **THEN** every trust field on the `MessageSource` is the value carried on the
  `ChannelInput`
- **AND** no value originates from a pipeline-level default
