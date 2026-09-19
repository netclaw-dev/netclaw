## MODIFIED Requirements

### Requirement: Shared gap-hydration engine

The system SHALL implement thread gap hydration (fetch history, filter by
cursor, classify for prompt-injection risk, merge adopted context, enqueue the
turn) in a single engine in the channel abstraction layer. Channel binding
actors with a safe, ordered, bounded history capability SHALL delegate hydration
to this engine. The engine SHALL take its security-relevant dependencies
(injection classifier, sender authorization callback) as required constructor
inputs.

Teams SHALL NOT synthesize or fetch historical adopted context until it has a
safe ordered-history capability with an explicit bounded contract. The Teams
adapter SHALL NOT require Microsoft Graph history permissions solely to satisfy
this parity requirement.

#### Scenario: All channels hydrate through one implementation

- **GIVEN** the Slack, Discord, and Mattermost binding actors
- **WHEN** each performs one-shot hydration at actor start
- **THEN** each delegates to the shared engine
- **AND** the per-channel code supplies only transport lookups and the cursor comparator

#### Scenario: Hydration contract behavior is unchanged

- **GIVEN** the existing cross-channel contract suite
- **WHEN** the hydration tests run (fetch at most once per lifetime, stash during hydration, re-run after supervised restart, adopted-context backfill)
- **THEN** every test passes for every channel without per-channel test changes

#### Scenario: Teams has no ordered history capability

- **GIVEN** a Teams thread binding without an explicit bounded history provider
- **WHEN** the binding starts or receives an authorized inbound message
- **THEN** it does not fetch unverified historical messages
- **AND** it does not request new Graph permissions
- **AND** it processes only the validated live ingress contract

### Requirement: Shared approval-response flow

The system SHALL implement text-approval parsing, cold-spawn approval
forwarding, requester validation, exact selected-key forwarding, and
pending-prompt resolution in a single shared flow. The pending-approval match
order SHALL be the same on every channel. Per-channel hooks SHALL be limited to
prompt rendering, transport callback validation, prompt presentation updates,
and Mattermost's synchronous webhook reply.

Teams SHALL use the shared flow after it validates its opaque card callback.
Teams SHALL retain only the correlation, nonce hash, prompt locator, and
consume/replay presentation state required for its transport. It SHALL NOT
invent policy, grant semantics, option ordering, or a second session approval
state machine.

#### Scenario: Wrong requester is rejected on every channel

- **GIVEN** a pending approval requested by user A
- **WHEN** user B attempts to approve it on Slack, Discord, Mattermost, or Teams
- **THEN** the shared flow rejects the response
- **AND** the channel posts or returns its wrong-requester result

#### Scenario: Mattermost synchronous reply hook

- **GIVEN** a Mattermost interactive-message approval
- **WHEN** the shared flow resolves it
- **THEN** the Mattermost hook sends the synchronous HTTP reply
- **AND** Discord, Slack, and Teams register no such hook

#### Scenario: Text approval resolves the earliest pending approval

- **GIVEN** two pending approvals that the same sender may approve
- **WHEN** that sender sends a text approval reply
- **THEN** every channel that accepts text approval resolves the earliest pending approval
- **AND** the next text approval reply resolves the second pending approval

#### Scenario: Cold Teams card action reaches the session authority

- **GIVEN** a valid Teams approval callback reaches a binding without local pending request memory
- **WHEN** the callback passes Teams callback validation
- **THEN** the shared flow forwards the exact option key to the session
- **AND** the session decides whether the call remains pending
- **AND** Teams does not authorize or deny the tool locally

### Requirement: Shared output-completion bookkeeping

The system SHALL implement turn-completion bookkeeping (cursor advance,
turn-in-flight state, reminder delivery settlement, empty-turn fallback, and
pending-prompt clearing) in a single engine. Persistence calls SHALL remain in
the actor: the engine SHALL return the events to persist and SHALL NOT invoke
Akka persistence. A channel-specific output hook SHALL handle output types that
only some channels support. A pipeline reinitialize abandons the turn in flight,
so the engine SHALL discard the pending cursor on every reinitialize, on every
channel.

Teams SHALL route the common completion and prompt-clearing lifecycle through
this engine while retaining its durable proactive-destination and delivery
records as transport-specific state.

#### Scenario: Channel-specific outputs go through the hook

- **GIVEN** a `SessionTitleOutput` for a Discord session
- **WHEN** the shared engine processes outputs
- **THEN** the Discord hook renames the thread
- **AND** a channel without that capability ignores the output in its hook

#### Scenario: Pipeline reinitialize discards the abandoned turn's cursor

- **GIVEN** a pipeline reinitialize while a turn is in flight
- **WHEN** the binding actor resets the engine and a later turn completes
- **THEN** every channel leaves the persisted cursor unmoved for the abandoned turn
- **AND** a channel with hydration includes the abandoned message in its next gap

#### Scenario: Teams settles an empty turn without a false delivery claim

- **GIVEN** a Teams session output turn completes without deliverable text
- **WHEN** the common output lifecycle processes completion
- **THEN** the Teams binding settles the turn consistently with other channels
- **AND** no reminder delivery is reported successful without a delivered message

### Requirement: Transport-failure escalation parity

The safe transport-call skeleton SHALL record telemetry, notify delivery
failure, and preserve the fail-loud contract: when the session feedback pipe
fails, the error SHALL propagate so supervision restarts the actor and
re-creates the pipeline. No channel SHALL swallow a feedback-pipe failure.
Typing indicators remain best-effort presentation effects and SHALL NOT block
or fail an otherwise deliverable response.

#### Scenario: Feedback-pipe failure faults every channel actor

- **GIVEN** a transport post failure whose delivery-failure feedback also fails
- **WHEN** the binding actor handles it on Slack, Discord, Mattermost, or Teams
- **THEN** the actor restarts under supervision
- **AND** the pipeline is re-created, observable as a second pipeline creation
