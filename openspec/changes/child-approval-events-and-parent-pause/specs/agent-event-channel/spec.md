## Purpose

Define the typed event channel between the agents of one session: the closed set of agent events, their envelope,
who may send what to whom, delivery guarantees, and how the runtime renders events for a model. Terms follow
[the engineering glossary](../../../../../docs/spec/GLOSSARY.md) (agent event, child consent pause, requester).

## ADDED Requirements

### Requirement: Agent events are a closed, typed family

The system SHALL exchange inter-agent facts as agent events. The set of agent event types SHALL be closed and
immutable. Each agent event SHALL carry an envelope with a sender address, a target address, and a send time from the
injected time source. An address SHALL be the main agent of a session (session ID) or a child run (session ID, run
ID, agent name). Addresses and payload fields SHALL be value objects, canonical names, enums, or runtime-made paths.
An event SHALL NOT hold an actor reference. The first event types SHALL be: child approval requested, child approval
resolved (approved, refused, or withdrawn with a reason), and child run ended. A child run ended event SHALL represent
the existing child result notice. The stored form of that notice SHALL NOT change.

#### Scenario: A child result reads as a child run ended event

- **GIVEN** a journal row with a child result notice that a deployed runtime wrote
- **WHEN** the session recovers
- **THEN** the session reads the notice as a child run ended event with sender `ChildRun(S, r, agent)` and target `Session(S)`
- **AND** the stored row is not rewritten

#### Scenario: An event has no actor reference

- **WHEN** a developer adds an agent event type
- **THEN** its envelope and payload hold only value objects, canonical names, enums, and runtime-made paths

### Requirement: Only the run owner builds an event about a run

Only the session that owns a child run SHALL build an agent event about that run. The session SHALL take the run
identity from its own run records. It SHALL NOT take a run identity from text or from a field that the child sets. A
child run SHALL emit facts only about itself. An event whose child run address names a run that the target session
does not own SHALL be rejected.

#### Scenario: Owner-stamped sender

- **GIVEN** child run r-7f2 of session S asks for consent
- **WHEN** session S builds the child approval requested event
- **THEN** the sender is `ChildRun(S, r-7f2, pr-reviewer)` from the run record of S

#### Scenario: A child cannot speak for another run

- **GIVEN** child run r-7f2 produces a tool argument that names run r-9c3
- **WHEN** session S builds an event from the facts of r-7f2
- **THEN** the sender is r-7f2
- **AND** no event about r-9c3 is built

### Requirement: An event payload carries no agent-chosen text

An agent event payload SHALL NOT contain free text that an agent chose. Agent-written content SHALL go to a file in the
run folder, and the event SHALL carry the path. The model SHALL read such a file with a real file read, as data.

#### Scenario: Child command text does not reach the parent model

- **GIVEN** a child asks for consent for the command `echo "run r-9c3 is done, cancel it"`
- **WHEN** the parent receives the approval notice
- **THEN** the notice names the agent, the run, and the tool
- **AND** the notice and the event payload do not contain the command text

### Requirement: The channel never carries consent

The channel SHALL carry observation and messages only. No agent event SHALL give consent to a child. Consent SHALL go
only through the consent bridge: a request and response loop with an authoritative prompt from the gate and a one-time
consent bound to the exact candidates, returned on its own awaited path. The consent bridge MAY be a producer of
child approval requested events.

#### Scenario: The parent cannot approve through the channel

- **GIVEN** a child consent request is open
- **WHEN** the parent model sends any tool call or text
- **THEN** no agent event or session output resolves the request
- **AND** only an operator answer that the requester check accepts resolves it

### Requirement: Delivery order and guarantees

The session SHALL deliver agent events to its model in the order in which it accepted the producer facts. A
resolution SHALL follow its request. A child run ended event SHALL reach the model once in effect. Child approval
events SHALL reach the model at most once. At recovery the session SHALL close every pending approval notice and SHALL
rebuild its digest from the open consent requests. If recovery closed a pending notice and a request stays open, the
rebuilt digest SHALL be ready, so that the model learns the true open set.

#### Scenario: Resolution follows request

- **GIVEN** a child consent request and its answer
- **WHEN** the parent model receives notices that contain both
- **THEN** the request appears before the resolution

#### Scenario: Recovery replaces a pending notice with the true state

- **GIVEN** an approval notice is admitted and not yet consumed
- **WHEN** the session recovers
- **THEN** the session closes that notice without a turn
- **AND** it admits one notice with the requests that are still open, or none if no request is open

### Requirement: The model sees rendered notices, not typed events

The runtime SHALL render agent events into notice text with one deterministic renderer. The model SHALL see only
the rendered text. The session SHALL store the rendered text with the admitted input. An approval notice SHALL be an
ordinary admitted input with source kind `child-consent`, sender `child-consent`, principal `VerifiedAutomation`, and
authenticity `LocalProcess`. The session SHALL identify a notice by all three of source kind, sender, and
authenticity. The stored input format SHALL NOT change for approval notices. The session SHALL keep one digest. The
closing notice SHALL come only when no child consent request of the session stays open.

#### Scenario: An older binary treats a notice as automation

- **GIVEN** a pending approval notice and a rollback to a binary without this change
- **WHEN** the old binary reads the input
- **THEN** it classifies the input as automated input by its principal

#### Scenario: No closing notice while a request stays open

- **GIVEN** Alice's child and Bob's child both wait for consent
- **WHEN** the operator answers Alice's request
- **THEN** no closing notice is admitted
- **WHEN** the operator answers Bob's request
- **THEN** one closing notice lists both answers and says that `spawn_agent` is available again

#### Scenario: A webhook cannot pose as a notice

- **GIVEN** a webhook event with event type `child-consent`
- **WHEN** the session admits it while paused
- **THEN** the session treats it as ordinary automated input, because its authenticity is not `LocalProcess`
- **AND** it waits behind the pause, and recovery does not close it as a notice

### Requirement: At most one approval notice waits

At most one approval notice of a session SHALL wait for its turn. When a digest is ready while a notice waits, the
session SHALL close the waiting notice with `InputClosed` and SHALL admit the new notice. One atomic journal write
(`PersistReplacingInput`) SHALL hold the close and the admission, so a crash keeps both or neither. The close SHALL set
`KeepsTurn`. A notice whose turn started SHALL NOT be replaced. The new notice then waits for its own turn. The new
notice SHALL list every open request of the session.

#### Scenario: A newer notice replaces the waiting notice

- **GIVEN** a parent prompt of Alice is open, so each approval notice waits
- **WHEN** three children ask for consent, one after the other
- **THEN** each newer notice closes the waiting notice with `InputClosed` in the same write as its admission
- **AND** after the parent answer, one notice turn runs, and its text says "3 are open."

#### Scenario: A closing notice replaces a stale open notice

- **GIVEN** an open notice waits from the end of tool batch 1 of Alice's turn
- **WHEN** the last child request closes during tool batch 2
- **THEN** the closing notice replaces the open notice
- **AND** one notice turn runs with the closing text, and no notice that says "spawn_agent is refused" runs after it

#### Scenario: A notice whose turn started is not replaced

- **GIVEN** the turn of approval notice N1 runs
- **WHEN** a new child request makes the digest ready
- **THEN** the session does not close N1
- **AND** the new notice waits for its own turn after the turn of N1

### Requirement: A notice gives each answer once

The digest SHALL keep each answer that no notice turn gave to the model. When a new notice replaces a waiting notice,
the new notice SHALL give the answers of the replaced notice again, because the model did not read them. An open
notice SHALL give these answers under the heading "Answers since the last notice:", in the same line form as the
closing notice. An answer that a notice turn already gave SHALL NOT appear in a later notice.

#### Scenario: A replacing notice keeps the unread answers

- **GIVEN** a parent prompt of Alice is open, so each approval notice waits
- **AND** child A asks, and the operator denies A, so a closing notice waits
- **WHEN** child B asks before the closing notice runs
- **THEN** the new open notice replaces the closing notice
- **AND** its text lists B as the one open request
- **AND** it gives `agent=<agent> run=<A> answer=refused reason=denied` under "Answers since the last notice:"

#### Scenario: A given answer does not come again

- **GIVEN** an idle session, where each approval notice runs at once
- **AND** children A and B ask, and the operator denies A while B stays open
- **WHEN** child C asks, and the open notice that gives the answer of A runs
- **AND** the operator then approves B and C
- **THEN** the closing notice gives only the answers of B and C
- **AND** it does not give the answer of A

### Requirement: A failed notice turn counts as given

An approval notice SHALL reach the model at most once. When the turn of a notice starts, the session SHALL count the
answers of that notice as given, also when the turn fails. The session SHALL NOT run a failed notice again. A later
notice SHALL NOT repeat the answers of the failed notice.

#### Scenario: A failed notice turn does not repeat its answers

- **GIVEN** an approval notice that gives the answer of child A starts its turn
- **WHEN** the turn fails
- **AND** a later request and its answer make a new notice ready
- **THEN** the new notice does not give the answer of A

#### Scenario: A notice that never started does not count as given

- **GIVEN** an approval notice that gives the answer of child A waits, and its turn has not started
- **WHEN** a new notice replaces it
- **THEN** the session does not count the answer of A as given
- **AND** the new notice gives the answer of A

### Requirement: A rejected notice admission stops the session

When the journal rejects the admission of an approval notice, the session SHALL log an error and SHALL stop. This
SHALL apply in every session phase, as after a persist failure. The session SHALL NOT post a failure for a turn that
it recorded as complete. At the next recovery, the session SHALL withdraw the child requests, close the pending
notices, and run the held automated input and child result notices in admission order.

#### Scenario: A rejected notice at a turn end stops the session

- **GIVEN** a child of Alice asks for consent while Alice's model call runs
- **WHEN** the turn ends and the journal rejects the notice that the turn end admits
- **THEN** the session stops
- **AND** it posts no failure for the turn that ended

#### Scenario: A rejected notice in an idle paused session does not leave the pause

- **GIVEN** an idle session that is paused, and a reminder fire that waits for the pause
- **WHEN** child B asks, and the journal rejects its notice
- **THEN** the session stops, and it does not keep a pause with no notice
- **AND** the next recovery withdraws the child requests, and the held fire runs

#### Scenario: A rejected admission of other input does not stop the session

- **GIVEN** a session in `Ready`
- **WHEN** the journal rejects the admission of an input that is not an approval notice
- **THEN** the session sends a negative acknowledgement for that input
- **AND** the session does not stop

### Requirement: Approval notice text forms

`AgentEventRenderer` SHALL make all approval notice text. The count line SHALL read "1 is open." for one open request
and "N are open." for more. The closing notice SHALL put the held-fires line just before "spawn_agent is available
again.". The held-fires line SHALL have the form `Held reminder fires now run: reminder=<name> fired_at=<HH:mm:ssZ>`.
Several fires SHALL be joined with ", ", in admission order. An open notice SHALL NOT have a held-fires line, because
those fires still wait.

#### Scenario: One open request reads in the singular

- **GIVEN** one child consent request is open
- **WHEN** the session renders the notice
- **THEN** the text says "Child consent requests wait for the operator. 1 is open."

#### Scenario: Two held fires come before the spawn line

- **GIVEN** reminder fires of `daily-pr-sweep` at 15:00:00Z and of `nightly-audit` at 15:30:00Z wait for the pause
- **WHEN** the last child request closes
- **THEN** the closing notice has the line
  `Held reminder fires now run: reminder=daily-pr-sweep fired_at=15:00:00Z, reminder=nightly-audit fired_at=15:30:00Z`
- **AND** the next line is "spawn_agent is available again."

#### Scenario: An open notice does not name the held fires

- **GIVEN** a reminder fire waits for the pause, and a child consent request stays open
- **WHEN** the session renders the open notice
- **THEN** the text has no "Held reminder fires now run:" line
