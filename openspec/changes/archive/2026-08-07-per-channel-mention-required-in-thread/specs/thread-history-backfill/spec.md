## ADDED Requirements

### Requirement: Mention-gated pending messages hydrate on the triggering mention

The adapter SHALL hydrate mention-gated pending messages into the adopted-context
window of the triggering mention's turn. When a per-channel `MentionRequiredInThread`
value holds un-mentioned thread messages as pending source-thread context (see the
`netclaw-input-adapters` capability), the adapter SHALL treat those pending messages
as the unsynced gap. When a bot mention creates the triggering authorized turn, the
adapter SHALL hydrate that gap using the existing authorized-sync-watermark gap
computation. This change introduces no new fetch path and no new watermark.

The mention-gated hold SHALL NOT advance the durable watermark. The watermark
advances only on `TurnCompleted` for the triggering mention. Therefore a mention
that arrives while the thread's binding actor is still live SHALL re-hydrate exactly
the messages held since the last completed turn — not only on a cold actor spawn.

Hydrated mention-gated messages SHALL pass through the same rules as any other
hydrated content, including the prompt-injection gate and per-sender trust and
audience resolution. No message gets a relaxed path because the mention gate held it.

#### Scenario: Held messages are hydrated on the triggering mention

- **GIVEN** a channel with `MentionRequiredInThread = true` and a live thread session
- **AND** three un-mentioned replies were held as pending since the last completed turn
- **WHEN** a user posts a reply that mentions the bot
- **THEN** the three held replies are hydrated into the adopted-context window before the mention
- **AND** the mention is the executable message for that turn

#### Scenario: A live-actor mention re-hydrates the gap

- **GIVEN** a thread whose binding actor is still live after its last completed turn
- **AND** un-mentioned messages accumulated in the thread since that turn
- **WHEN** a mention arrives for that thread
- **THEN** the adapter hydrates the gap from the durable watermark
- **AND** the re-hydration is not limited to a cold actor spawn

#### Scenario: Held messages do not advance the watermark

- **GIVEN** the durable watermark for a thread is `X`
- **AND** un-mentioned messages with ordering keys greater than `X` are held as pending
- **WHEN** no mention has yet created a turn
- **THEN** the durable watermark remains `X`
- **AND** the held messages remain in the gap for the next mention

#### Scenario: Held messages pass the prompt-injection gate on hydration

- **GIVEN** a held un-mentioned message whose text triggers the injection detector at `High` risk
- **WHEN** a mention triggers hydration of the gap
- **THEN** that message is excluded from the merged input with a warning log
- **AND** the mention gate does not exempt held messages from the injection gate
