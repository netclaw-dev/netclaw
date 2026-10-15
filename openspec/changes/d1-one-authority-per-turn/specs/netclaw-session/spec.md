## ADDED Requirements

### Requirement: One authority per turn for buffered input

The session SHALL run each turn under the authority of one requester. Use the
[engineering glossary](../../../../docs/spec/GLOSSARY.md#requester) for the
term. Two inputs have the same requester when the session, audience, trust
boundary, channel type, sender, and principal classification all match.

- The session SHALL put into one batch only the leading buffered inputs that
  share one requester. The first input that has another requester SHALL stay
  in the buffer, in order, with every input after it.
- A waiting input SHALL start its own turn under its own recorded authority.
  The recorded authority includes the trust level, the tool scope, and the
  prompt audience that the session admitted with that input.
- A running turn SHALL accept a joining input at a tool boundary only when it
  has the same requester as the turn.
- A follow-up turn after a finished turn or a failed turn SHALL adopt the
  recorded authority of its first input. It SHALL NOT reuse the authority of
  the earlier turn.
- A replay of a turn SHALL continue that turn under its own authority.
- Input order SHALL hold across turns. An input SHALL NOT start a turn before
  an earlier buffered input.

#### Scenario: Same-requester input joins a running turn

- **GIVEN** Alice has a running turn
- **AND** Alice sends a second message while a tool runs
- **WHEN** the tool batch finishes
- **THEN** the second message joins the running turn
- **AND** the next model call runs under Alice's authority

#### Scenario: Another requester waits for its own turn

- **GIVEN** Alice has a running turn
- **AND** Bob sends a message in the same thread
- **WHEN** the tool batch finishes
- **THEN** Bob's message does not join Alice's turn
- **AND** the session finishes Alice's turn
- **AND** Bob's message then starts a turn under Bob's recorded trust, tool scope, and prompt audience

#### Scenario: A mixed buffer splits at each change of requester

- **GIVEN** the buffer holds a Personal input from Alice, a Public input from Bob, and a second Personal input from Alice, in that order
- **WHEN** the session drains the buffer after a turn
- **THEN** it runs three separate turns in that order
- **AND** each turn runs under the recorded authority of its own input
- **AND** no turn holds inputs of two requesters

#### Scenario: A follow-up adopts the recorded authority of its first input

- **GIVEN** a turn for Alice finished or failed
- **AND** a buffered input from Bob waits
- **WHEN** the follow-up turn starts
- **THEN** it uses the tool scope and prompt audience that the session recorded with Bob's input
- **AND** it does not use Alice's tool scope or prompt audience

#### Scenario: A replay continues its own turn

- **GIVEN** a delivery retry replays a turn for Alice
- **AND** a buffered input from Bob waits
- **WHEN** the replay runs
- **THEN** the replay continues Alice's turn under Alice's authority
- **AND** Bob's input waits until that turn ends
