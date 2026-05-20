## ADDED Requirements

### Requirement: Channel-owned interactive callback endpoint safeguards

A channel-owned inbound HTTP callback endpoint SHALL authenticate every inbound
request before it mutates any session state.

Such an endpoint is registered by a channel whose transport delivers interactive
responses only over inbound HTTP, such as Mattermost interactive message
buttons. Authentication SHALL be carried by a per-action credential
(implementations include single-use opaque action tokens stored server-side, or
HMAC signatures over the payload) that is unforgeable, replay-resistant, and
scoped to the channel that minted it. The endpoint SHALL run an ACL evaluation
on the resolved sender, SHALL reject requests whose channel does not match the
credential's bound channel, and SHALL be registered only when the owning
channel is enabled with interactive approvals configured. The endpoint SHALL
fail closed on invalid configuration and SHALL NOT create new autonomous
sessions — it routes only into existing sessions identified by the callback
payload.

#### Scenario: Forged callback payload rejected before state change

- **GIVEN** a channel-owned interactive callback endpoint is registered
- **WHEN** an inbound request arrives with a missing, unknown, expired, or
  replayed per-action credential
- **THEN** the daemon rejects the request
- **AND** no approval state is mutated and no session is created

#### Scenario: Callback endpoint enforces ACL on the resolved sender

- **GIVEN** an authenticated callback request resolves to a sender
- **WHEN** the endpoint evaluates the request
- **THEN** ACL evaluation runs on the resolved sender before the response is
  applied
- **AND** a denied sender's callback is rejected with a structured deny reason

#### Scenario: Callback endpoint rejects channel-bound credential reuse on a different channel

- **GIVEN** a per-action credential was minted for one channel
- **WHEN** a callback arrives whose payload `channel_id` does not match the
  credential's bound channel
- **THEN** the daemon rejects the request
- **AND** no approval state is mutated

#### Scenario: Callback endpoint not registered when channel is disabled

- **GIVEN** the owning channel is disabled or has no interactive approvals
  configured
- **WHEN** the daemon starts
- **THEN** the channel-owned callback endpoint is not registered
- **AND** no inbound HTTP surface is exposed for that channel

#### Scenario: Callback endpoint routes only into existing sessions

- **GIVEN** an authenticated, ACL-allowed callback request
- **WHEN** the endpoint processes the request
- **THEN** the response is routed by session identity to an existing session
- **AND** the endpoint never creates a new autonomous session
