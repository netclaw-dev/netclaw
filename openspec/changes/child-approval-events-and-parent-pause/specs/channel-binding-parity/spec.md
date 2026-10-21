## MODIFIED Requirements

### Requirement: Shared approval-response flow

The system SHALL implement text-approval parsing, cold-spawn approval forwarding, and pending-prompt resolution in a single shared flow. The requester identity check SHALL execute inside the shared flow. The pending-approval match rule SHALL be the same on every channel. A text approval reply SHALL resolve a pending approval only when the sender can approve exactly one pending approval. Per-channel hooks SHALL be limited to prompt rendering and, for Mattermost only, the synchronous webhook reply.

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

- **GIVEN** two pending approvals, and the sender may approve only the earliest one
- **WHEN** that sender sends a text approval reply
- **THEN** Slack, Discord, and Mattermost each resolve the earliest pending approval

#### Scenario: Text approval with two pending approvals resolves neither

- **GIVEN** two pending approvals that the same sender may approve
- **WHEN** that sender sends a text approval reply
- **THEN** Slack, Discord, and Mattermost each resolve neither approval
- **AND** each channel tells the sender to use the buttons on the prompt

> Note: the earlier rule resolved the earliest pending approval. With parallel
> child prompts that do not name the child, model text could describe one
> command while a text reply approved another. The owner chose on 21 October
> 2026 that a text reply works only when exactly one approval is pending.

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

A child consent prompt SHALL show buttons only. It SHALL NOT include "reply with" text, and the channel SHALL NOT post
a text-only fallback for it. When the channel cannot post the buttons, the binding SHALL report the prompt as
unavailable, and the session SHALL withdraw the request.

#### Scenario: Failed button post withdraws the child request

- **GIVEN** a Slack binding fails to post the buttons of a child prompt
- **WHEN** it reports the prompt as unavailable
- **THEN** no text prompt is posted
- **AND** the session withdraws the request with the reason that Netclaw could not show it

### Requirement: Closed and replayed prompts

When the session emits a prompt-closed output, each channel SHALL find the prompt by its call ID, redraw it as closed
with the reason text, remove its pending entry, and record that the prompt is cleared. The TUI SHALL remove the prompt.
The redraw SHALL NOT name the child run or the agent. When the session sends a prompt again to a new subscriber,
Slack, Discord, Mattermost, and the TUI SHALL NOT show a call ID that they already track a second time. Headless output
SHALL print a prompt and its close with no child identity.

#### Scenario: Cancelled child prompt is redrawn as closed

- **GIVEN** a pending child prompt in a Slack thread
- **WHEN** the session emits a prompt-closed output with the reason "cancelled by parent"
- **THEN** the prompt shows "This approval request was withdrawn because the task that asked for it was cancelled. Nothing ran."
- **AND** the prompt has no buttons

#### Scenario: Replayed prompt is not shown twice

- **GIVEN** a Slack binding and a TUI client each track a pending child prompt
- **WHEN** the session restarts and sends the open prompt again
- **THEN** neither shows a second prompt
