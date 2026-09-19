## Purpose

Provide authenticated, replay-safe Teams Adaptive Card approvals that survive
binding passivation and daemon restart without silently authorizing tool calls.
Teams renders the session approval protocol. It does not define a second policy.

## ADDED Requirements

### Requirement: Teams approval actions are bound to persisted pending state

An Adaptive Card payload SHALL be correlation data, not authorization. The
authoritative binding actor SHALL validate its version, tenant, conversation,
submitting sender/requester policy, session ID, call ID, selected option, nonce,
expiry, current pending status, and consume state against persisted pending
state. The binding actor SHALL serialize a consume-once state transition before
forwarding `ToolInteractionResponse`. That transition is atomic only within the
existing actor/persistence boundary; no wider distributed atomicity is claimed.

#### Scenario: Valid requester approval is consumed once

- **WHEN** the original requester submits a current valid approval action
- **THEN** the authoritative binding consumes the pending request before forwarding the selected decision
- **AND** the selected decision reaches the session once

#### Scenario: Forged or replayed action is rejected

- **WHEN** an action has an invalid version, nonce, sender, tenant, conversation,
  call, option, expiry, pending state, or consume state
- **THEN** it is rejected without changing tool approval state

### Requirement: Teams renders the supplied approval options

The Teams adapter SHALL render each option from
`ToolInteractionRequest.Options`, in supplied order. Each Adaptive Card action
SHALL carry its supplied canonical `ApprovalOptionKey`. The adapter SHALL NOT
map a Teams-specific approve action to another key. It SHALL NOT add, remove,
reorder, or infer an approval option.

The card SHALL use bounded existing display contracts for the tool, request,
candidate or pattern summary, scope summary, complex-command notice, and
adopted-context notice. Display facts SHALL NOT authorize an action. The card
SHALL NOT persist raw tool arguments or card JSON for display recovery.

#### Scenario: A reusable shell approval exposes all supplied scopes

- **GIVEN** the session supplies Once, This chat, Always here, Always anywhere,
  and Deny
- **WHEN** Teams renders the approval card
- **THEN** the card has those five labels in that order
- **AND** each action carries its corresponding canonical key

#### Scenario: A one-shot approval stays one-shot

- **GIVEN** the session supplies only Once and Deny for a messy or unsafe-to-reuse call
- **WHEN** Teams renders the approval card
- **THEN** the card has only Once and Deny
- **AND** Teams does not add a session or persistent choice

#### Scenario: An MCP approval keeps its persistent label

- **GIVEN** the session supplies an MCP persistent option labelled Always allow this tool
- **WHEN** Teams renders the approval card
- **THEN** the card uses that supplied label
- **AND** its action carries `approve_everywhere`

### Requirement: Teams validates the offered key before consumption

The binding actor SHALL persist the ordered canonical keys it offered before it
posts a pending card. It SHALL validate a submitted key against that stored set
before it records the consume transition. It SHALL forward the same validated
key in `ToolInteractionResponse`.

The state SHALL retain no raw nonce, tool arguments, request display, card JSON,
service URL, token, or SDK object. It SHALL retain only bounded values required
to validate the action and render its terminal state. The existing session
journal option set does not replace this binding state.

#### Scenario: A supplied persistent key reaches the session unchanged

- **GIVEN** a current card offered `approve_always`
- **WHEN** the authorized requester selects that action
- **THEN** the binding records one consume transition
- **AND** it sends `approve_always` as `ToolInteractionResponse.SelectedKey`

#### Scenario: A fabricated key is rejected before consume

- **GIVEN** a current card offered only Once and Deny
- **WHEN** a caller submits `approve_everywhere` with an otherwise valid correlation and nonce
- **THEN** the binding rejects the action
- **AND** it does not consume the pending approval
- **AND** it does not send a tool interaction response

#### Scenario: A pre-parity pending card fails closed

- **GIVEN** a recovered pending Teams approval has no persisted offered-key set
- **WHEN** a caller submits its legacy action token
- **THEN** the binding reports the approval as unavailable
- **AND** it does not send a tool interaction response
- **AND** it does not map the legacy token to a canonical option key

### Requirement: Approval actions recover deterministically

The Teams channel SHALL rehydrate the same binding for a valid still-pending
approval action after passivation or daemon restart. Expired, unavailable, or
already-completed state SHALL produce a deterministic terminal result and SHALL
never be silently dropped. A terminal card-update failure is an outbound
presentation failure only; it SHALL NOT change an approval result already
determined by persisted state.

#### Scenario: Action after daemon restart

- **WHEN** a user submits a valid still-pending card action after daemon restart
- **THEN** the binding rehydrates and processes the action

#### Scenario: Interruption after consume

- **WHEN** the pending state was consumed and the process is interrupted before the visible card update is confirmed
- **THEN** recovery does not execute the tool decision twice
- **AND** a later card presentation retry cannot reopen or reconsume the decision

#### Scenario: Action after completion

- **WHEN** a user submits a card action after its request has completed
- **THEN** the card action receives a terminal expired or completed result
