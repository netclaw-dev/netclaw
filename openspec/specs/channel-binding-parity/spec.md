# channel-binding-parity Specification

## Purpose

Guarantee that the Slack, Discord, and Mattermost binding actors run gap hydration, approval response handling, output-completion bookkeeping, and transport-failure escalation through single shared implementations in the channel abstraction layer. Per-channel hooks exist only for genuine transport differences. This prevents the drift class where a fix or a security check lands in one channel and misses the others.

## Requirements

### Requirement: Shared gap-hydration engine

The system SHALL implement thread gap hydration (fetch history, filter by cursor, classify for prompt-injection risk, merge adopted context, enqueue the turn) in a single engine in the channel abstraction layer. Channel binding actors SHALL delegate hydration to this engine. The engine SHALL take its security-relevant dependencies (injection classifier, sender authorization callback) as required constructor inputs.

#### Scenario: All channels hydrate through one implementation

- **GIVEN** the Slack, Discord, and Mattermost binding actors
- **WHEN** each performs one-shot hydration at actor start
- **THEN** each delegates to the shared engine
- **AND** the per-channel code supplies only transport lookups and the cursor comparator

#### Scenario: Hydration contract behavior is unchanged

- **GIVEN** the existing cross-channel contract suite
- **WHEN** the hydration tests run (fetch at most once per lifetime, stash during hydration, re-run after supervised restart, adopted-context backfill)
- **THEN** every test passes for every channel without per-channel test changes

### Requirement: Cursor ordering by injected comparator

The shared engine SHALL store cursors as strings, which matches the persisted `CursorAdvanced` format for every channel. Cursor comparison SHALL use a comparator the channel supplies. The Discord comparator SHALL order numeric snowflake strings identically to unsigned 64-bit numeric comparison. Discord SHALL NOT use plain ordinal string comparison.

#### Scenario: Discord snowflake ordering across digit lengths

- **GIVEN** two snowflake strings with different digit counts, such as an 18-digit and a 19-digit value
- **WHEN** the Discord comparator orders them
- **THEN** the result equals the numeric `ulong` ordering
- **AND** a unit test proves the equivalence for cross-digit-length pairs and for boundary values

### Requirement: Shared approval-response flow

The system SHALL implement text-approval parsing, cold-spawn approval forwarding, and pending-prompt resolution in a single shared flow. The requester identity check SHALL execute inside the shared flow. Per-channel hooks SHALL be limited to prompt rendering, the pending-approval match order, and, for Mattermost only, the synchronous webhook reply.

#### Scenario: Wrong requester is rejected on every channel

- **GIVEN** a pending approval requested by user A
- **WHEN** user B attempts to approve it on any channel
- **THEN** the shared flow rejects the response
- **AND** the channel posts its wrong-requester warning

#### Scenario: Mattermost synchronous reply hook

- **GIVEN** a Mattermost interactive-message approval
- **WHEN** the shared flow resolves it
- **THEN** the Mattermost hook sends the synchronous HTTP reply
- **AND** Discord and Slack register no such hook

#### Scenario: Channel match order picks the same candidate as before

- **GIVEN** two pending approvals that the same sender may approve
- **WHEN** that sender sends a text approval reply
- **THEN** Slack resolves the earliest pending approval
- **AND** Discord and Mattermost resolve the most recent pending approval

> Note: this match order is a real difference between the pre-consolidation
> copies. Slack selected its candidate with `FindIndex` (earliest match);
> Discord and Mattermost selected it with `LastOrDefault` (most recent match).
> The shared lookup keeps one requester check and takes the order as a
> required `ApprovalMatchOrder` input, so each channel keeps the selection it
> had. Which order is correct is a separate product question, tracked outside
> the introducing change.

### Requirement: Shared output-completion bookkeeping

The system SHALL implement turn-completion bookkeeping (cursor advance, turn-in-flight state, reminder delivery settlement, empty-turn fallback, pending-prompt clearing) in a single engine. Persistence calls SHALL remain in the actor: the engine SHALL return the events to persist and SHALL NOT invoke Akka persistence. A channel-specific output hook SHALL handle output types that only some channels support.

#### Scenario: Channel-specific outputs go through the hook

- **GIVEN** a `SessionTitleOutput` for a Discord session
- **WHEN** the shared engine processes outputs
- **THEN** the Discord hook renames the thread
- **AND** a channel without that capability ignores the output in its hook

#### Scenario: Pipeline reinitialize keeps each channel's cursor discipline

- **GIVEN** a pipeline reinitialize while a turn is in flight
- **WHEN** the binding actor resets the engine
- **THEN** Slack discards the pending cursor
- **AND** Discord and Mattermost keep it, which preserves their current behavior

> Note: the consolidation surfaced this divergence. Slack clears the pending
> cursor on reinitialize; Discord and Mattermost keep it, so a later
> `TurnCompleted` can commit the cursor of a turn that the reinitialize
> abandoned. That is a possible latent defect in the Discord and Mattermost
> behavior, tracked as a product question outside the introducing change.

### Requirement: Transport-failure escalation parity

The safe transport-call skeleton SHALL record telemetry, notify delivery failure, and preserve the fail-loud contract: when the session feedback pipe fails, the error SHALL propagate so supervision restarts the actor and re-creates the pipeline. No channel SHALL swallow a feedback-pipe failure.

#### Scenario: Feedback-pipe failure faults every channel actor

- **GIVEN** a transport post failure whose delivery-failure feedback also fails
- **WHEN** the binding actor handles it on any channel
- **THEN** the actor restarts under supervision
- **AND** the pipeline is re-created, observable as a second pipeline creation
