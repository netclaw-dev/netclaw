## ADDED Requirements

### Requirement: Observe outcome for non-allowed users in active threads

The ACL layer SHALL support a third evaluation outcome — Observe — alongside
Allow and Deny. When `AllowedUserIds` is populated and the sender is not on the
list, the ACL SHALL return Observe instead of Deny if a thread session already
exists for the inbound message's thread. The Observe outcome SHALL carry
`PrincipalClassification = UntrustedExternal` and the same audience and
provenance that an Allow outcome would produce for that channel context.

The `AclOutcome` enum SHALL be defined with three values: `Allow`, `Observe`,
and `Deny`. The `IAclDecision` interface SHALL expose `AclOutcome Outcome` in
addition to the existing `bool IsAllowed` property. `IsAllowed` SHALL return
`true` only for `Allow`, not for `Observe`. This ensures existing code that
checks `!IsAllowed` to drop messages will safely drop Observe messages until
conversation actors are updated to handle them.

Both `SlackAclDecision` and `DiscordAclDecision` SHALL provide an `Observe`
factory method that produces a decision with the Observe outcome.

#### Scenario: Non-allowed user in active thread receives Observe

- **GIVEN** `AllowedUserIds` contains `["U111"]`
- **AND** a thread session exists for the current thread
- **WHEN** user `U999` (not in allowed list) sends a message in that thread
- **THEN** `EvaluateInbound` returns `AclOutcome.Observe`
- **AND** the decision carries `Principal = UntrustedExternal`

#### Scenario: Non-allowed user without active thread receives Deny

- **GIVEN** `AllowedUserIds` contains `["U111"]`
- **AND** no thread session exists for the current thread
- **WHEN** user `U999` (not in allowed list) sends a message
- **THEN** `EvaluateInbound` returns `AclOutcome.Deny` with reason `user_not_allowed`

#### Scenario: AllowedUserIds empty bypasses Observe logic

- **GIVEN** `AllowedUserIds` is empty (all users allowed)
- **AND** a thread session exists
- **WHEN** any user sends a message
- **THEN** `EvaluateInbound` returns `AclOutcome.Allow`
- **AND** the Observe path is not evaluated

#### Scenario: Observe decision IsAllowed returns false

- **GIVEN** an ACL decision with `Outcome = AclOutcome.Observe`
- **WHEN** `IsAllowed` is checked
- **THEN** `IsAllowed` returns `false`

### Requirement: Conversation actor Observe forwarding

Conversation actors SHALL forward Observe messages to the thread binding actor
instead of dropping them. The forwarded inbound message SHALL carry
`IsObserver = true` so that downstream pipeline components can distinguish
observer messages from authorized messages. The conversation actor SHALL NOT
change routing policy behavior for Observe messages — the existing routing
decision (which already accounts for `threadExists`) SHALL be used as-is.

#### Scenario: Slack conversation forwards Observe message

- **GIVEN** the Slack ACL returns `AclOutcome.Observe` for an inbound message
- **AND** the routing policy returns `ContinueOnly` (thread exists)
- **WHEN** the conversation actor processes the message
- **THEN** the message is forwarded to the thread binding actor
- **AND** `SlackThreadInbound.IsObserver` is `true`
- **AND** `SlackThreadInbound.Principal` is `UntrustedExternal`

#### Scenario: Discord conversation forwards Observe message

- **GIVEN** the Discord ACL returns `AclOutcome.Observe` for an inbound message
- **AND** the routing policy returns `ContinueOnly` (thread exists)
- **WHEN** the conversation actor processes the message
- **THEN** the message is forwarded to the session binding actor
- **AND** `DiscordThreadInbound.IsObserver` is `true`

#### Scenario: Deny is still dropped

- **GIVEN** the ACL returns `AclOutcome.Deny`
- **WHEN** the conversation actor processes the message
- **THEN** the message is dropped with a telemetry counter
- **AND** no message reaches the binding actor

## MODIFIED Requirements

### Requirement: Channel and sender allow checks

The system SHALL evaluate channel and sender policy before turn dispatch. The
evaluation SHALL support three outcomes: Allow, Observe, and Deny.
`EvaluateInbound` SHALL accept a `bool threadExists` parameter to determine
whether the Observe outcome is applicable.

#### Scenario: Sender allowed, channel allowed

- **GIVEN** sender and channel are explicitly allowed
- **WHEN** a message arrives
- **THEN** ACL evaluation returns `AclOutcome.Allow`

#### Scenario: Sender disallowed, no active thread

- **WHEN** sender is not allowed by policy
- **AND** no thread session exists
- **THEN** ACL evaluation returns `AclOutcome.Deny`

#### Scenario: Sender disallowed, active thread exists

- **WHEN** sender is not allowed by policy
- **AND** a thread session exists for the message's thread
- **THEN** ACL evaluation returns `AclOutcome.Observe`
- **AND** the decision carries `Principal = UntrustedExternal`
