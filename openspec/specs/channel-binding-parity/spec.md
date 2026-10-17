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

The system SHALL implement text-approval parsing, cold-spawn approval forwarding, and pending-prompt resolution in a single shared flow. The requester identity check SHALL execute inside the shared flow. The pending-approval match order SHALL be the same on every channel. Per-channel hooks SHALL be limited to prompt rendering and, for Mattermost only, the synchronous webhook reply.

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

#### Scenario: Text approval resolves the earliest pending approval

- **GIVEN** two pending approvals that the same sender may approve
- **WHEN** that sender sends a text approval reply
- **THEN** Slack, Discord, and Mattermost each resolve the earliest pending approval
- **AND** the next text approval reply resolves the second pending approval

> Note: the consolidation found a real difference between the pre-consolidation
> copies. Slack selected its candidate with `FindIndex` (earliest match);
> Discord and Mattermost selected it with `LastOrDefault` (most recent match).
> The maintainer resolved the difference: the earliest pending approval wins on
> every channel. The shared lookup keeps one requester check and hard-codes the
> earliest match, so no channel supplies an order.

### Requirement: Shared output-completion bookkeeping

The system SHALL implement turn-completion bookkeeping (cursor advance, turn-in-flight state, reminder delivery settlement, empty-turn fallback, pending-prompt clearing) in a single engine. Persistence calls SHALL remain in the actor: the engine SHALL return the events to persist and SHALL NOT invoke Akka persistence. A channel-specific output hook SHALL handle output types that only some channels support. A pipeline reinitialize abandons the turn in flight, so the engine SHALL discard the pending cursor on every reinitialize, on every channel.

#### Scenario: Channel-specific outputs go through the hook

- **GIVEN** a `SessionTitleOutput` for a Discord session
- **WHEN** the shared engine processes outputs
- **THEN** the Discord hook renames the thread
- **AND** a channel without that capability ignores the output in its hook

#### Scenario: Pipeline reinitialize discards the abandoned turn's cursor

- **GIVEN** a pipeline reinitialize while a turn is in flight
- **WHEN** the binding actor resets the engine and a later turn completes
- **THEN** Slack, Discord, and Mattermost each leave the persisted cursor unmoved
- **AND** the abandoned turn's messages appear in the next gap hydration

### Requirement: Transport-failure escalation parity

The safe transport-call skeleton SHALL record telemetry, notify delivery failure, and preserve the fail-loud contract: when the session feedback pipe fails, the error SHALL propagate so supervision restarts the actor and re-creates the pipeline. No channel SHALL swallow a feedback-pipe failure.

#### Scenario: Feedback-pipe failure faults every channel actor

- **GIVEN** a transport post failure whose delivery-failure feedback also fails
- **WHEN** the binding actor handles it on any channel
- **THEN** the actor restarts under supervision
- **AND** the pipeline is re-created, observable as a second pipeline creation

### Requirement: Channel binding lifetime follows session lifetime

Slack, Discord, and Mattermost bindings SHALL keep their session output
subscription while the session remains active. A binding SHALL NOT stop on an
independent idle timeout. After a binding receives committed
`SessionDeactivated`, it SHALL drain its session pipeline and stop. A
conversation parent SHALL NOT have an independent idle timeout. It SHALL stay
active while any binding child remains and stop after its last binding child
terminates.

#### Scenario: Binding stays active while its session remains active

- **GIVEN** a Slack, Discord, or Mattermost binding has an active session
- **WHEN** the former binding idle timeout would expire
- **THEN** the binding stays active
- **AND** it keeps its session output subscription

#### Scenario: Binding stops after committed session deactivation

- **GIVEN** a binding receives committed `SessionDeactivated`
- **WHEN** its session pipeline drain completes
- **THEN** the binding stops
- **AND** it does not restart its pipeline for that deactivation

#### Scenario: Parent stays active while another binding child remains

- **GIVEN** a conversation parent has two binding children
- **WHEN** one child terminates after session deactivation
- **THEN** the parent remains active
- **AND** it keeps the other child

#### Scenario: Parent stops after its last binding child

- **GIVEN** a conversation parent has one binding child
- **WHEN** that child terminates after session deactivation
- **THEN** the parent stops

Use the [engineering glossary](../../../docs/spec/GLOSSARY.md) for shared terms.

### Requirement: A failed post reports the turn that it belongs to

A channel binding SHALL send `DeliveryFailed` with the number of the turn whose output failed to post. The session accepts feedback only for its eligible turn (see `netclaw-session`, "A failed post on a reminder turn is visible to the operator"). A wrong number makes the session drop the report as stale feedback, or blame the wrong turn.

A binding posts the output of a turn while the turn runs. It learns the turn number from `TurnCompleted`. Thus the binding SHALL hold the first failure of the turn (kind and message) and SHALL send the report when `TurnCompleted` arrives, with `TurnCompleted.TurnNumber`. The binding SHALL NOT send the report when the post fails. The binding SHALL NOT use the number of the last completed turn, because that number names the previous turn.

All three bindings SHALL follow these rules:

- A binding sends at most one `DeliveryFailed` for each turn.
- A binding sends no report for a turn that posted at least one output. Part of the reply reached the thread, and a retry would post that part again. Slack follows this rule today. Discord and Mattermost SHALL follow it.
- A failed post of the empty-turn fallback is a failed post of the turn that the fallback answers. The binding SHALL report it with that turn number.
- A pipeline reinitialize abandons the turn in flight, so the binding SHALL discard the held failure.
- The binding SHALL hold only a failed post of the turn's own output: the text, the file, and the error text that the output engine posts for the turn. A failed post of anything else is not output of a turn. This includes a warning post (ingress closed, a blocked or unscanned live message, an attachment warning, the backfill warning, the wrong-requester warning) and an approval-prompt post. Such a failure keeps its Warning log and its counter. The binding SHALL NOT hold it, because it can occur between turns, and the first held failure would then replace the real failure of the next turn. Slack follows this rule today. Discord and Mattermost SHALL follow it.

The shared output engine and the binding actor own this decision. The held failure is actor-local. It lives for one turn.

#### Scenario: A failed post in turn 3 reports turn 3

- **GIVEN** a binding completed turn 2
- **WHEN** the output of turn 3 fails to post
- **THEN** the binding sends one `DeliveryFailed` with `TurnNumber = 3` when `TurnCompleted` for turn 3 arrives
- **AND** this holds for Slack, Discord, and Mattermost

#### Scenario: Counterexample - the report does not carry the previous turn

- **GIVEN** a binding completed turn 2 and no turn before it
- **WHEN** the output of turn 3 fails to post
- **THEN** no `DeliveryFailed` carries `TurnNumber = 2` or `TurnNumber = 0`

#### Scenario: Two failed posts in one turn send one report

- **GIVEN** turn 3 holds two text outputs
- **WHEN** both posts fail
- **THEN** the binding sends one `DeliveryFailed` for turn 3, with the kind and message of the first failure

#### Scenario: Counterexample - a turn with a posted output sends no report

- **GIVEN** turn 3 holds two text outputs
- **WHEN** the first post fails and the second post succeeds
- **THEN** the binding sends no `DeliveryFailed` for turn 3

#### Scenario: Counterexample - a failed warning post does not replace the failure of the next turn

- **GIVEN** the warning post for a blocked message fails with the error text `warning post rejected`, between two turns
- **WHEN** the output post of turn 5 then fails with the error text `output post rejected`
- **THEN** the binding sends one `DeliveryFailed` with `TurnNumber = 5`
- **AND** its `ErrorMessage` contains `output post rejected` and not `warning post rejected`
- **AND** this holds for Slack, Discord, and Mattermost

#### Scenario: A failed fallback post reports the turn it answers

- **GIVEN** turn 3 produced no output, so the binding posts the empty-turn fallback
- **WHEN** the fallback post fails
- **THEN** the binding sends one `DeliveryFailed` with `TurnNumber = 3`

#### Scenario: A Mattermost reminder reply with a bad token reports the recorded turn

- **GIVEN** a `current_session` reminder runs in a Mattermost thread, and the session completes turn 1
- **WHEN** the Mattermost API rejects the bot token on the reply
- **THEN** the session receives one `DeliveryFailed` with `TurnNumber = 1` and `FailureKind = TransportFailure`
