## MODIFIED Requirements

### Requirement: Shared approval-response flow

The system SHALL implement text-approval parsing, cold-spawn approval forwarding, and pending-prompt resolution in a single shared flow. The requester identity check SHALL execute inside the shared flow. The pending-approval match rule SHALL be the same on every channel. When a child prompt (lifetime "until closed") is among two or more pending approvals that the sender can approve, a text approval reply SHALL resolve none and SHALL tell the sender to use the buttons. With parent prompts only, a text approval reply SHALL resolve the earliest pending approval that the sender can approve. When the session refuses an answer for a prompt that the binding holds as withdrawn, expired, or already answered, the binding SHALL remove the prompt, record that it is cleared, and redraw it with no buttons. Per-channel hooks SHALL be limited to prompt rendering and, for Mattermost only, the synchronous webhook reply.

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

- **GIVEN** two pending parent approvals that the same sender may approve
- **WHEN** that sender sends a text approval reply
- **THEN** Slack, Discord, and Mattermost each resolve the earliest pending approval
- **AND** the next text approval reply resolves the second pending approval

#### Scenario: Text approval with a pending child prompt resolves neither

- **GIVEN** a pending parent approval and a pending child prompt that the same sender may approve
- **WHEN** that sender sends a text approval reply
- **THEN** Slack, Discord, and Mattermost each resolve neither approval
- **AND** each channel tells the sender to use the buttons on the prompt

#### Scenario: A refused answer clears a restored child prompt

- **GIVEN** a binding that restored a child prompt from its journal after the session forgot it
- **WHEN** the session refuses an answer for that prompt as withdrawn
- **THEN** the binding removes the prompt, records that it is cleared, and redraws it with no buttons
- **AND** a later text approval reply answers a live parent prompt

> Note: the owner chose on 21 October 2026 that a text reply works only when
> exactly one approval is pending. The PR 2 review narrowed the rule to sets
> with a child prompt. A text-only Mattermost binding has no buttons, so the
> earliest-first rule stays its only way to answer parallel parent calls.

### Requirement: Shared output-completion bookkeeping

The system SHALL implement turn-completion bookkeeping (cursor advance, turn-in-flight state, reminder delivery settlement, empty-turn fallback, pending-prompt clearing) in a single engine. Persistence calls SHALL remain in the actor: the engine SHALL return the events to persist and SHALL NOT invoke Akka persistence. A channel-specific output hook SHALL handle output types that only some channels support. A pipeline reinitialize abandons the turn in flight, so the engine SHALL discard the pending cursor on every reinitialize, on every channel. At turn completion and at a turn error, the engine and the TUI SHALL clear only prompts whose lifetime is the turn. A prompt whose lifetime is "until closed" SHALL stay pending until an answer or a prompt-closed output. A tracked prompt record SHALL carry the lifetime in an optional field. A record without the field SHALL read as a turn prompt.

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

#### Scenario: A child prompt outlives the parent turn

- **GIVEN** a child consent prompt with lifetime "until closed" is pending
- **WHEN** the parent turn completes or fails
- **THEN** Slack, Discord, Mattermost, and the TUI keep the prompt pending
- **AND** a button answer still resolves it

#### Scenario: A cold-spawned binding keeps a child prompt

- **GIVEN** a binding journaled a child prompt with lifetime "until closed"
- **WHEN** the binding is cold-spawned and a turn completes
- **THEN** the binding keeps the prompt pending

## ADDED Requirements

### Requirement: Child prompts have buttons only

A child consent prompt SHALL show buttons only. The channel SHALL identify a child prompt only by its lifetime "until
closed", never by the shape of its call ID. It SHALL NOT include "reply with" text, and the channel SHALL NOT post
a text-only fallback for it. When the channel cannot post the buttons, the binding SHALL report the prompt as
unavailable, and the session SHALL withdraw the request. A Mattermost binding with no callback URL SHALL report every
child prompt as unavailable.

#### Scenario: Failed button post withdraws the child request

- **GIVEN** a Slack binding fails to post the buttons of a child prompt
- **WHEN** it reports the prompt as unavailable
- **THEN** no text prompt is posted
- **AND** the session withdraws the request with the reason that Netclaw could not show it

#### Scenario: Text-only Mattermost refuses a child prompt

- **GIVEN** a Mattermost binding with no callback URL
- **WHEN** the session emits a child prompt
- **THEN** the binding posts no text prompt and reports the prompt as unavailable

#### Scenario: A parent prompt with a child-shaped call ID keeps its reply hint

- **GIVEN** a parent prompt with the lifetime "until turn end" whose call ID contains "/subagent-approval/"
- **WHEN** a text-only Mattermost binding posts it
- **THEN** the prompt is a text prompt with its "reply with" hint
- **AND** the binding does not report it as unavailable

### Requirement: Closed and replayed prompts

When the session emits a prompt-closed output, each channel SHALL find the prompt by its call ID, redraw it as closed
with the reason text, remove its pending entry, and record that the prompt is cleared. The reason SHALL be "answered" or
"withdrawn" with a withdraw reason. A prompt that closed because a person answered it SHALL be redrawn as answered, and
SHALL NOT say "withdrawn" or "Nothing ran". The TUI SHALL remove the prompt.
The redraw SHALL NOT name the child run or the agent. When the session sends a prompt again to a new subscriber,
Slack, Discord, Mattermost, and the TUI SHALL NOT show a call ID that they already track a second time. Headless output
SHALL print a prompt and its close with no child identity.

#### Scenario: Cancelled child prompt is redrawn as closed

- **GIVEN** a pending child prompt in a Slack thread
- **WHEN** the session emits a prompt-closed output with the reason "cancelled by parent"
- **THEN** the prompt shows "This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran."
- **AND** the prompt has no buttons

#### Scenario: An answer on another client closes the prompt

- **GIVEN** two clients show the same child prompt
- **WHEN** the operator approves it in the first client
- **THEN** the second client gets a prompt-closed output with the reason "answered"
- **AND** no text in the second client says "withdrawn" or "Nothing ran"

#### Scenario: Replayed prompt is not shown twice

- **GIVEN** a Slack binding and a TUI client each track a pending child prompt
- **WHEN** the session restarts and sends the open prompt again
- **THEN** neither shows a second prompt
