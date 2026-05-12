## ADDED Requirements

### Requirement: Bootstrap-time transcript seeding for agent-initiated channel sessions

A channel adapter SHALL seed the new session's transcript with the
full posted payload and the platform-assigned message id whenever
it accepts an agent-initiated proactive-post tool call and creates a
new channel session as a direct result. The bootstrap handler SHALL
append the payload to the new session's persisted transcript before
the bootstrap acknowledgment is returned to the originating tool
call. The seeded entry SHALL appear in any subsequent LLM context
assembled for the new session. The "full posted payload" includes
text, attachments, rich blocks, and any other content that posted
under the platform-assigned message id.

The seeded entry SHALL be retained even if the originating tool call
times out after the seeding completed; an in-flight ack failure does
not invalidate a successfully persisted seed.

The seed SHALL be the only source of agent-authored content for the
session until that session itself produces an LLM turn. The bootstrap
write SHALL NOT depend on the channel's inbound-message echo path
to populate the transcript.

#### Scenario: Bootstrap acknowledgment is returned only after seed is persisted

- **GIVEN** an agent-initiated proactive-post tool call has posted to
  the target channel and obtained a platform-assigned message id
- **WHEN** the channel adapter sends the bootstrap protocol message
  to the new session's binding actor
- **THEN** the binding actor persists the posted payload as an entry
  in the new session's transcript
- **AND** the binding actor returns the bootstrap acknowledgment
  only after the persistence completes

#### Scenario: User reply on the new thread sees the seeded payload in LLM context

- **GIVEN** a proactive-post session was bootstrapped with seeded
  payload P
- **WHEN** a user replies in the same thread or conversation,
  triggering an LLM turn in the new session
- **THEN** the assembled LLM context for that turn contains P as a
  prior assistant-authored entry, ordered before the user reply

#### Scenario: Tool call success implies seed is in transcript

- **GIVEN** an agent-initiated proactive-post tool call has returned
  success to the calling session
- **WHEN** any subsequent LLM context is assembled for the new
  channel session
- **THEN** that context contains the posted payload as an entry in
  the transcript

### Requirement: Session identity equivalence between bootstrap and inbound

The session id minted at bootstrap time SHALL equal the session id
that the channel adapter's inbound-message ingress will compute for
any user reply in the same thread or conversation. The bootstrap and
inbound paths SHALL share a single derivation rule for session
identity, so that a reply lands on the same session that was
bootstrapped.

#### Scenario: Bootstrap and inbound resolve to the same session id

- **GIVEN** an agent-initiated proactive-post tool call has obtained
  a platform-assigned thread or conversation id
- **WHEN** the channel adapter mints a session id for the bootstrap
  protocol message
- **AND** the channel adapter's inbound ingress later computes a
  session id for a reply in the same thread or conversation
- **THEN** the two session ids are equal

#### Scenario: Reply routes to the bootstrapped session

- **GIVEN** a proactive-post session was bootstrapped with session id S
- **WHEN** a user reply arrives in the same thread or conversation
- **THEN** the channel adapter routes the reply to the existing
  binding actor for session id S
- **AND** does not create a second session for the same thread or
  conversation

### Requirement: Idempotent seeding keyed by platform message id

The seeded transcript entry SHALL be keyed by the platform-assigned
message id. A retried or replayed bootstrap protocol message bearing
the same message id SHALL NOT result in a duplicate seeded entry. A
retry SHALL ack normally without further side effects.

#### Scenario: Replayed bootstrap does not double-seed

- **GIVEN** a proactive-post session has already been seeded with
  payload P keyed by message id M
- **WHEN** the bootstrap protocol message for M is redelivered to the
  binding actor
- **THEN** the binding actor returns the bootstrap acknowledgment
- **AND** the session transcript still contains exactly one entry for
  message id M

#### Scenario: Crash between seed and ack is recoverable

- **GIVEN** a binding actor has persisted a seeded entry for message
  id M but the bootstrap acknowledgment was not delivered to the
  originating tool
- **WHEN** the originating tool retries the bootstrap protocol
  message for M
- **THEN** the binding actor returns the bootstrap acknowledgment
- **AND** the session transcript still contains exactly one entry for
  message id M

### Requirement: Isolation of seed writes from turn-lifecycle dispatch

A seed write SHALL NOT emit the events that an LLM-produced
assistant turn would emit. The channel adapter SHALL NOT dispatch
`TurnCompleted`, `TurnRecorded`, or any subscriber-facing assistant
turn event for a seed write. Persistence of the seed entry to the
transcript SHALL proceed independently of the dispatch path.

The channel adapter's inbound bot-message filter (where present, for
loop prevention) SHALL remain unchanged in scope. The filter SHALL
NOT be relied on as a mechanism for capturing seed content; the seed
write is the sole mechanism.

#### Scenario: Seed write does not fire turn-completion events

- **GIVEN** an output subscriber is attached to a proactive-post
  session before bootstrap
- **WHEN** the bootstrap protocol message is processed and the seed
  is persisted
- **THEN** the subscriber receives no `TurnCompleted` event for the
  seed
- **AND** the subscriber receives no `TurnRecorded` event for the
  seed
- **AND** subsequent LLM-produced turns in the session still fire
  events to the subscriber normally

#### Scenario: Inbound bot-message filter is not the seeding mechanism

- **GIVEN** the channel adapter has an inbound-message filter that
  drops bot-authored events for loop prevention
- **WHEN** the bootstrap protocol message creates a new session and
  the channel later echoes the agent's posted message back through
  the inbound path
- **THEN** the inbound echo is dropped by the existing filter
- **AND** the seeded transcript entry from the bootstrap write
  remains intact and is the only source of the posted payload in the
  session transcript

### Requirement: Seed persistence across session actor restart

The seeded transcript entry SHALL be persisted such that it survives
binding actor restart, daemon restart, or any other recovery event
that recreates the session actor from durable state. After recovery,
the assembled LLM context for the next turn SHALL contain the seed.

#### Scenario: Seed is present after binding actor recreation

- **GIVEN** a proactive-post session was bootstrapped and the seed
  was persisted
- **WHEN** the binding actor is stopped and a new instance is created
  from durable state for the same session id
- **THEN** the recreated binding actor's loaded transcript contains
  the seeded entry
- **AND** any subsequent LLM context assembled for the session
  contains the seed

### Requirement: Scope exclusion for non-bootstrap agent content

This capability SHALL govern only the bootstrap-time seeding of a
new channel session created as a direct result of an agent-initiated
proactive-post tool call. Agent-authored content produced by an
existing session's normal turn lifecycle — including reentrant
reminder delivery into an existing session, normal LLM replies in
an existing session, and any other in-session output — SHALL remain
governed by the persisted turn lifecycle defined in
`netclaw-session` and SHALL NOT be additionally seeded by the
bootstrap mechanism.

#### Scenario: Reentrant reminder content is not seeded

- **GIVEN** an existing channel session is active
- **AND** a reminder with reentrant delivery (delivers into the
  existing session) fires
- **WHEN** the channel adapter routes the reminder content into the
  existing session
- **THEN** the bootstrap-time seeding mechanism is not invoked
- **AND** the reminder content is recorded in the session transcript
  through the normal persisted turn lifecycle

#### Scenario: Normal in-session LLM reply is not seeded

- **GIVEN** an active channel session
- **WHEN** the session's LLM produces a reply that the channel
  adapter posts to the channel
- **THEN** the bootstrap-time seeding mechanism is not invoked
- **AND** the reply is recorded in the session transcript through
  the normal persisted turn lifecycle
