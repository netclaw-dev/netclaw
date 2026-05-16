## ADDED Requirements

### Requirement: Deferred hydration is re-armed until it completes

A threaded channel adapter SHALL re-arm thread-history hydration whenever a
hydration pass fetches a non-empty gap but finds no message in that gap with
`authority-at-inclusion=authorized` to anchor a turn. Such a pass is
**deferred**: the adapter SHALL NOT count its once-per-actor-lifetime hydration
as consumed. A pass that confirmed there is nothing to adopt (empty thread, or
every fetched message at or below the durable watermark) or that enqueued an
authorized turn is instead **completed**.

While hydration is re-armed, the first subsequent **authorized** inbound SHALL
perform the deferred hydration: the adapter SHALL fetch the current thread gap,
classify it, and merge that gap as the adopted-context window preceding the
authorized inbound, which remains the executable message for the turn. The
adapter SHALL enqueue exactly one authorized turn for that inbound and SHALL
then revert to normal fetch-free inbound handling.

Re-arming SHALL be cleared once a re-armed hydration completes (whether or not
the resulting gap was empty). An adapter whose hydration has completed SHALL
NOT fetch thread history again on subsequent inbounds; re-arming therefore
never causes a fetch on an ordinary inbound.

This requirement exists because a proactively-created thread's binding actor
begins its lifetime when the agent posts the thread root — before any
authorized human inbound exists. Its startup hydration necessarily defers,
because the only gap message is the bot-authored root and a bot is not an
allowed user. Without re-arming, the bot root is never adopted and the first
human reply executes with no record of the message that opened the thread.

#### Scenario: Proactively-created thread adopts its bot root on the first authorized reply

- **GIVEN** an agent-initiated proactive post created a thread whose root is the
  bot's own message
- **AND** the binding actor's startup hydration ran while that bot root was the
  only message in the thread and deferred for lack of an authorized trigger
- **WHEN** an authorized user replies in that thread within the same binding
  actor lifetime
- **THEN** the adapter performs the deferred hydration
- **AND** the bot-authored thread root is included in the authorized turn's
  adopted-context window
- **AND** the authorized reply is the executable message for that turn

#### Scenario: Ordinary inbound after a completed hydration does not re-fetch history

- **GIVEN** a binding actor whose hydration completed, either by enqueuing an
  authorized turn or by confirming an empty gap
- **WHEN** a further authorized inbound arrives
- **THEN** the adapter does not fetch thread history for that inbound
- **AND** no adopted-context window is recomputed from server-side history

#### Scenario: Unauthorized inbound while hydration is deferred keeps it re-armed

- **GIVEN** a binding actor whose startup hydration deferred
- **WHEN** a non-allowed user sends a threaded message before any authorized
  inbound arrives
- **THEN** the adapter does not perform the deferred hydration
- **AND** the adapter does not dispatch a turn
- **AND** hydration remains re-armed for the next authorized inbound

#### Scenario: Re-armed hydration fetch failure is non-fatal

- **GIVEN** a binding actor whose startup hydration deferred
- **WHEN** an authorized inbound arrives and the re-armed thread-history fetch
  fails
- **THEN** the authorized inbound is still executed as a turn without an
  adopted-context window
- **AND** hydration remains re-armed so a later authorized inbound can retry

#### Scenario: Discord DM never defers and never re-arms

- **GIVEN** a Discord DM session, whose flat conversation has no distinct
  thread root
- **WHEN** the binding actor's startup hydration runs
- **THEN** no fetched entry satisfies the thread-root predicate, so no
  bot-authored entry is hydrated
- **AND** hydration does not defer on account of a bot root
- **AND** the adapter does not re-arm hydration
