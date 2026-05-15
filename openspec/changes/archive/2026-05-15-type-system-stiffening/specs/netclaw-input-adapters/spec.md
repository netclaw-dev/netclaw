## ADDED Requirements

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

#### Scenario: Pipeline does not synthesize trust context

- **WHEN** the message-source factory builds a `MessageSource` from a
  `ChannelInput`
- **THEN** every trust field on the `MessageSource` is the value carried on the
  `ChannelInput`
- **AND** no value originates from a pipeline-level default
