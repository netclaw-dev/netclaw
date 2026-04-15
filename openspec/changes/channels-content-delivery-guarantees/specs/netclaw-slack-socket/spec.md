## ADDED Requirements

### Requirement: Structured routing-policy ignore reasons

`SlackRoutingPolicy.Evaluate` SHALL return a `SlackRoutingDecision` record
struct carrying a `Kind` and, when the decision is `Ignore`, a non-null
`SlackRoutingIgnoreReason` value. The `SlackRoutingIgnoreReason` enum SHALL
enumerate exactly the distinct branches that can return `Ignore`:
`NoContent`, `WrongKind`, `HiddenMessage`, `UnsupportedSubtype`,
`DmNotAllowed`, `DmMentionRequired`, `ChannelMentionRequired`.

`SlackConversationActor` SHALL log the specific ignore reason on every
`slack_event_filtered reason=routing_policy_ignore` emission and SHALL
pass the reason to `ChannelTelemetry.RecordSlackEventFiltered` so each
branch produces a distinct metric label. The existing telemetry API
signature SHALL be extended in place rather than renamed or replaced.

Unit tests in `SlackRoutingPolicyTests` SHALL assert the exact
`IgnoreReason` value on every case that expects `Ignore`, so adding a new
`Ignore`-returning branch without matching test coverage SHALL fail the
build.

#### Scenario: File-share with empty files is ignored with specific reason

- **GIVEN** a `SlackInboundMessage` with `Subtype = "file_share"`,
  `Files = null`, and empty `Text`
- **WHEN** `SlackRoutingPolicy.Evaluate` is called
- **THEN** the returned decision has `Kind = Ignore`
- **AND** `IgnoreReason = SlackRoutingIgnoreReason.NoContent`

#### Scenario: DM with mention required but no mention is ignored with specific reason

- **GIVEN** a DM `SlackInboundMessage` without a bot mention
- **AND** `mentionRequiredInDm = true`
- **WHEN** `SlackRoutingPolicy.Evaluate` is called
- **THEN** the returned decision has `Kind = Ignore`
- **AND** `IgnoreReason = SlackRoutingIgnoreReason.DmMentionRequired`

#### Scenario: Hidden message with files is ignored before subtype check

- **GIVEN** a `SlackInboundMessage` with `Hidden = true` and
  `Subtype = "file_share"` and non-empty `Files`
- **WHEN** `SlackRoutingPolicy.Evaluate` is called
- **THEN** the returned decision has `Kind = Ignore`
- **AND** `IgnoreReason = SlackRoutingIgnoreReason.HiddenMessage`
- **AND** the hidden check fires before the subtype check so repeat
  deliveries of the same event do not get re-routed

#### Scenario: DM file_share with image is routed

- **GIVEN** a DM `SlackInboundMessage` with `Subtype = "file_share"`,
  a non-empty `Files` array, and a non-empty `Text`
- **AND** `allowDirectMessages = true`, `mentionRequiredInDm = false`
- **WHEN** `SlackRoutingPolicy.Evaluate` is called
- **THEN** the returned decision has `Kind = StartOrContinue`
- **AND** `IgnoreReason` is null

#### Scenario: Gateway log surfaces the specific reason

- **GIVEN** `SlackConversationActor` receives an inbound message that
  `SlackRoutingPolicy.Evaluate` rejects with
  `IgnoreReason = UnsupportedSubtype`
- **WHEN** the actor logs the drop
- **THEN** the log line contains `reason=routing_policy_ignore ignoreReason=UnsupportedSubtype`
- **AND** `ChannelTelemetry.RecordSlackEventFiltered` is called with a
  reason string that distinguishes `UnsupportedSubtype` from other
  ignore branches

#### Scenario: Successful routing produces no ignore reason

- **GIVEN** a DM `SlackInboundMessage` with non-empty text and allowed
  configuration
- **WHEN** `SlackRoutingPolicy.Evaluate` is called
- **THEN** the returned decision has `Kind = StartOrContinue`
- **AND** `IgnoreReason` is null
