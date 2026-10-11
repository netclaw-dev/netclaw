## ADDED Requirements

### Requirement: A fresh interactive chat creates its session on first input

The CLI SHALL connect a fresh interactive chat without a daemon session until it receives its first input.
The CLI SHALL preserve explicit Create, Resume, Keep, and headless session contracts.
Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.

#### Scenario: Open and quit leaves no empty session

- **GIVEN** the user opens a fresh interactive chat
- **WHEN** the user quits without text or an initial message
- **THEN** the daemon creates no session for that chat

#### Scenario: The first input establishes one session

- **GIVEN** the user opens a fresh chat after another chat
- **WHEN** the user submits text
- **THEN** the client creates one new session and sends the text there
- **AND** it does not send the text to the earlier session

#### Scenario: Resume retains the selected identity

- **GIVEN** the user selects a session to resume
- **WHEN** the connection attaches successfully
- **THEN** later input targets that session
- **AND** the client does not create an unrelated session

### Requirement: Accepted local inputs retain order and visible outcomes

The CLI SHALL retain accepted local inputs in order through connection and attachment retries.
The CLI SHALL display each permanent rejection or uncertain delivery outcome.
The CLI SHALL NOT automatically resend an input after uncertain RPC dispatch.

#### Scenario: Duplicate connection events do not duplicate input

- **GIVEN** two local inputs wait for attachment
- **WHEN** duplicate connection events occur before attachment completes
- **THEN** the client attempts the two inputs once each in their original order

#### Scenario: An interrupted RPC remains uncertain

- **GIVEN** the client dispatches an input RPC
- **WHEN** the connection fails before its response
- **THEN** the client reports unconfirmed delivery
- **AND** it does not automatically replay that input

#### Scenario: Queued caller cancellation prevents dispatch

- **GIVEN** a cancellable command waits before transport dispatch
- **WHEN** its caller cancels it
- **THEN** the client removes that command
- **AND** it makes no RPC for that command

#### Scenario: Admission response cannot reverse completed model state

- **GIVEN** a turn-complete event reaches the client before its text RPC response
- **WHEN** the text RPC response confirms admission
- **THEN** the UI retains the completed turn state
- **AND** it does not restore the Generating status

### Requirement: Normal quit resolves admission within a two-second limit

The CLI SHALL allow at most two seconds for prior input admission after normal quit starts.
The limit SHALL apply once to all prior local inputs.
The CLI SHALL reject later inputs and SHALL NOT wait for model completion.
The CLI SHALL report queued unsent text separately from an unresolved active RPC.
The final unresolved receipt SHALL remain visible after the TUI closes.

#### Scenario: Admission completes before the deadline

- **GIVEN** the user submits text and requests normal quit
- **WHEN** the daemon confirms admission within two seconds
- **THEN** the CLI can close without a model response
- **AND** it does not discard the close result before UI disposal

#### Scenario: The deadline separates uncertain and unsent text

- **GIVEN** one input RPC remains unresolved and another local input waits behind it
- **WHEN** the two-second deadline expires
- **THEN** the CLI reports unconfirmed delivery for the active input
- **AND** it reports the queued input as unsent
- **AND** it starts no later input RPC

#### Scenario: Repeated quit shares one close operation

- **GIVEN** normal quit has started
- **WHEN** the user requests quit again and submits later text
- **THEN** the client retains one close operation
- **AND** it rejects the later text

### Requirement: Offline bootstrap and client observers retain isolation

Offline bootstrap SHALL create no client ActorSystem.
An explicit transition to daemon chat SHALL use one local client ActorSystem for that host.
A blocked observer SHALL NOT prevent client commands from completion.

Identity redo SHALL capture the daemon generation before it writes config.
If the daemon already runs, an absent generation SHALL block that write with a visible error.
After the user chooses guided chat, the CLI SHALL confirm a healthy daemon before navigation.
If the daemon already ran before the write, that confirmation SHALL require a newer generation.
If the daemon was down, the CLI SHALL use the existing start and health check.
The existing wizard readiness component SHALL own that wait.
A skip or quit SHALL cancel preparation without dispatch of the hidden input.

#### Scenario: Bootstrap exits without chat

- **WHEN** the user completes offline bootstrap without chat
- **THEN** the CLI creates no client ActorSystem

#### Scenario: Bootstrap transitions to chat

- **WHEN** the user explicitly enters daemon chat after bootstrap
- **THEN** the host creates one local client ActorSystem
- **AND** host shutdown terminates that system

#### Scenario: An observer blocks while a command proceeds

- **GIVEN** one client event observer blocks
- **WHEN** another client command is ready to execute
- **THEN** that command can complete without release of the observer

#### Scenario: Identity redo waits for the daemon config reload

- **GIVEN** identity redo captures generation 1 before its config write
- **WHEN** the user chooses guided chat while generation 1 remains healthy
- **THEN** the CLI does not enter chat
- **AND** a healthy generation 2 permits the hidden input through normal chat admission

#### Scenario: An absent generation blocks identity persistence

- **GIVEN** the daemon already runs but its probe fails or omits its generation
- **WHEN** identity redo attempts its config write
- **THEN** the CLI reports the probe failure
- **AND** config and identity files remain unchanged

#### Scenario: Quit cancels the guided chat handoff

- **GIVEN** the CLI waits for the saved identity generation
- **WHEN** the user quits
- **THEN** the CLI cancels preparation
- **AND** it sends no hidden input
