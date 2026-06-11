## ADDED Requirements

### Requirement: Approval-paused turn lifecycle preserves original context

The persisted session turn lifecycle SHALL preserve the original turn context across approval pause, approval response, tool redrive, follow-up LLM calls, continuation tool calls, and turn completion. The context SHALL remain active until the resumed turn completes, fails, or is explicitly abandoned by a new user message.

#### Scenario: Context remains active through continuation LLM call

- **GIVEN** a recovered approval response redrives a parked tool batch
- **WHEN** the tool result is appended and the session invokes the continuation LLM call
- **THEN** the continuation call uses the restored turn context
- **AND** the exposed tool list is filtered for the original turn audience and boundary

#### Scenario: Context remains active through continuation tool call

- **GIVEN** a recovered approval redrive has resumed a turn
- **WHEN** the continuation LLM response requests another tool call
- **THEN** that tool call is dispatched with the same restored turn context
- **AND** approval capability and channel type match the original turn

#### Scenario: Context clears when resumed turn completes

- **GIVEN** a resumed approval-paused turn completes successfully
- **WHEN** `TurnCompleted` is emitted
- **THEN** the session clears the active approval turn context
- **AND** the next user message starts with a new turn context

### Requirement: Approval recovery tests cover context directly

Session approval recovery tests SHALL include direct coverage for turn-context construction, persistence, restoration, and projection into tool and memory contexts. End-to-end cold-recovery tests SHALL remain for user-visible recovery behavior, but field-by-field context propagation SHOULD be covered through focused tests where possible.

#### Scenario: Direct projection test replaces field-only integration coverage

- **GIVEN** a persisted turn context has audience, boundary, channel type, requester, approval capability, and adopted-context state
- **WHEN** the session projects that context into tool execution and memory-policy inputs
- **THEN** the projected values match the persisted context
- **AND** the assertion does not require a full actor cold-recovery scenario for each individual field

#### Scenario: End-to-end recovery test remains for user-visible behavior

- **GIVEN** a user approves a pending tool prompt after session recovery
- **WHEN** the session redrives the parked tool batch
- **THEN** the user observes the tool result and final assistant response
- **AND** no duplicate approval prompt is emitted for the approved call
