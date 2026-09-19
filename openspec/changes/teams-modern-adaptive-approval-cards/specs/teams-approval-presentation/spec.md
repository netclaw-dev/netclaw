## Purpose

Define the secure modern Adaptive Card presentation that Teams uses for a session-owned Netclaw tool approval.

## ADDED Requirements

### Requirement: Teams uses the modern 1.5 approval-card presentation

Teams SHALL emit approval cards that target Adaptive Cards schema 1.5. Each card SHALL have a Fluent icon header, the `NETCLAW SECURITY CONTROL` subtitle, a semantic banner, and a structured table when display fields exist. The card SHALL provide bounded screen-reader text without opaque callback values.

#### Scenario: Standard shell approval card

- **GIVEN** a pending `shell_execute` approval
- **WHEN** Teams creates its card
- **THEN** the card shows the `ShieldLock` header and an Accent banner
- **AND** the table uses `Tool` and `Command` rows
- **AND** the card declares schema version `1.5`

#### Scenario: MCP approval card

- **GIVEN** a pending MCP tool approval
- **WHEN** Teams creates its card
- **THEN** the display table uses `Invocation` for the request value
- **AND** the card does not add a risk level without a canonical risk source

### Requirement: Teams keeps approval buttons data-driven and opaque

Teams SHALL render exactly the `ToolInteractionRequest.Options` sequence that the session supplied. Each approval button SHALL use `Action.Execute`, verb `netclaw-approval`, and the existing `correlation`, `nonce`, and `action` data names. Display text and button style SHALL not create approval authority.

#### Scenario: Restricted option set remains restricted

- **GIVEN** a pending request with only an approve-once option and deny
- **WHEN** Teams creates its card
- **THEN** the card contains only those two actions in that order
- **AND** deny uses destructive style while approve-once uses positive style

#### Scenario: Persistent option key remains exact

- **GIVEN** a pending request with a persistent approval option
- **WHEN** Teams creates its card
- **THEN** the action data contains the supplied canonical option key unchanged
- **AND** the card does not add a Teams-local approval key

### Requirement: Terminal cards state the recorded transport outcome

Teams SHALL replace the source pending approval card in place with an actionless modern terminal card by returning that card in the `Action.Execute` invoke response. Teams SHALL NOT post Granted, Denied, Already Processed, or Unavailable terminal outcomes as separate follow-up Teams messages. A granted card SHALL state that execution remains pending. A denied card SHALL state that the user rejected the request. Teams SHALL not use a granted or denied card for a malformed, stale, wrong-requester, or unavailable callback.

#### Scenario: Accepted approval presents an authorization state

- **GIVEN** the session accepts a non-deny option
- **WHEN** Teams handles the `Action.Execute` callback
- **THEN** the invoke response replaces the source pending card with the Good `ShieldCheckmark` presentation
- **AND** it shows the accepted approval scope, the accepted timestamp, and `Pending execution`
- **AND** it contains no approval actions
- **AND** Teams posts no additional terminal approval message

#### Scenario: Explicit deny presents a blocked state

- **GIVEN** the session accepts the explicit deny option
- **WHEN** Teams handles the `Action.Execute` callback
- **THEN** the invoke response replaces the source pending card with the Attention `ShieldDismiss` presentation
- **AND** it shows `User rejected the request`
- **AND** it contains no approval actions
- **AND** Teams posts no additional terminal approval message

#### Scenario: Unavailable callback remains neutral

- **GIVEN** a callback is no longer available or already processed
- **WHEN** Teams handles the source `Action.Execute` callback
- **THEN** the invoke response replaces the source pending card with a neutral information or warning presentation
- **AND** it does not claim that an approval was granted or denied
- **AND** Teams posts no additional terminal approval message

### Requirement: Card expiry remains presentation-only

Teams SHALL render an expired card as an actionless warning. When the requester submits an expired card, the `Action.Execute` invoke response SHALL replace that source card in place with the expired presentation. Because expiry is presentation-only and the session approval remains pending, Teams SHALL separately post one new pending approval card with a fresh nonce. Expiry SHALL not create a core denial or execution. Teams SHALL not mutate an expired card automatically without a requester action.

#### Scenario: Expired card reissues a fresh callback binding

- **GIVEN** a pending approval card has expired
- **WHEN** the requester submits the expired card
- **THEN** the invoke response replaces the source card with an actionless expired card with the expiry timestamp
- **AND** Teams separately posts one new pending card with a fresh nonce
- **AND** the session approval remains pending

### Requirement: Elevated presentation needs a canonical risk source

Teams SHALL not parse a command or infer a risk classification to select the elevated card. Teams SHALL select the elevated visual only when a transport-neutral canonical risk value reaches the presentation boundary.

#### Scenario: No canonical risk value exists

- **GIVEN** a pending request has no canonical risk value
- **WHEN** Teams creates its card
- **THEN** Teams creates the standard pending card
- **AND** the card does not show a fabricated safe or high risk level
