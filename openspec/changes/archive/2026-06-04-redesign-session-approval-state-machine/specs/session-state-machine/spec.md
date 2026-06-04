## ADDED Requirements

### Requirement: Approval turn state is explicit

The session actor SHALL maintain explicit approval turn state for approval-paused work, separate from the coarse `SessionPhase` lifecycle. The approval turn state SHALL identify whether there is no active approval turn, a running turn, a turn waiting for one or more approval responses, a recovered waiting turn, a redrive in progress, or an abandoned approval turn being healed. Approval response handling SHALL consult this approval turn state instead of inferring the turn solely from nullable transport metadata or scattered pending dictionaries.

#### Scenario: Live approval request records waiting state

- **GIVEN** a session is processing a turn with a valid turn context
- **WHEN** a tool call emits an approval request
- **THEN** the session records approval turn state for the original turn context
- **AND** the waiting state includes the pending approval call id

#### Scenario: Recovered approval records recovered waiting state

- **GIVEN** a session recovers journaled pending approval state
- **WHEN** recovery completes
- **THEN** the session records recovered approval turn state with the restored turn context
- **AND** later approval responses are handled against that state

#### Scenario: Abandoned approval state heals the transcript

- **GIVEN** a recovered approval turn is waiting for a user response
- **WHEN** the user sends a new message instead of answering the approval prompt
- **THEN** the session transitions the approval turn state to abandoned
- **AND** the unanswered assistant tool calls are closed with synthetic tool results before the new turn is processed

### Requirement: Approval redrive uses actor-owned state transitions

Approval response handling SHALL transition through actor-owned approval states when redriving a parked tool batch. A redrive SHALL use the restored turn context attached to the approval turn state, transition the coarse `SessionPhase` through legal transitions, and clear approval turn state only after the parked batch and continuation path have completed or been abandoned.

#### Scenario: Ready approval response enters redrive state

- **GIVEN** a recovered session is in `Ready` with approval turn state waiting for call `call-1`
- **WHEN** a valid `ToolInteractionResponse` for `call-1` arrives
- **THEN** the session transitions to a redrive approval state
- **AND** the coarse phase transitions from `Ready` to `Processing`

#### Scenario: Redrive completion clears approval state

- **GIVEN** a session is redriving a recovered approval turn
- **WHEN** the parked tool batch completes and the continuation turn finishes
- **THEN** the session clears the approval turn state
- **AND** no pending or resolved approval state remains for the completed call ids
