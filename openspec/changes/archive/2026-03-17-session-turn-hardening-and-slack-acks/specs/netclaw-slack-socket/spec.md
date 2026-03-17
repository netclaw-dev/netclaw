## ADDED Requirements

### Requirement: In-thread acknowledgement for hidden long-running work

The Slack adapter SHALL post one brief in-thread acknowledgement when a turn has not yet produced visible text or file output but hidden session activity shows the turn is actively working. The acknowledgement threshold SHALL be based on adapter-local heuristics and SHALL NOT expose raw tool-call details.

#### Scenario: Slack posts one acknowledgement after hidden tool activity
- **GIVEN** a Slack turn has produced no visible text or file output
- **AND** the adapter has observed enough hidden tool activity to classify the turn as actively working
- **WHEN** the turn is still in progress
- **THEN** Netclaw posts one brief acknowledgement in the same thread
- **AND** it does not post additional acknowledgements for that same turn

#### Scenario: Fast turn does not emit acknowledgement
- **GIVEN** a Slack turn produces its visible reply before the hidden-activity threshold is reached
- **WHEN** the turn completes normally
- **THEN** Netclaw does not post a separate acknowledgement message

### Requirement: Empty-turn fallback respects visible terminal output

The Slack adapter SHALL emit the generic empty-turn fallback only when a turn completes without any visible terminal output. A prior acknowledgement alone does not count as terminal output, but a visible reply, file upload, or explicit error message does.

#### Scenario: Streamed reply suppresses generic fallback
- **GIVEN** the Slack adapter already posted the visible reply for a turn from streamed or buffered text
- **WHEN** `TurnCompleted` arrives
- **THEN** the adapter does not post the generic empty-turn fallback message

#### Scenario: Acknowledgement alone does not suppress terminal failure handling
- **GIVEN** the adapter posted a progress acknowledgement for an in-flight turn
- **AND** the turn completes without a visible reply or file output
- **WHEN** the terminal outcome is resolved
- **THEN** the adapter still posts the explicit error outcome or generic empty-turn fallback
- **AND** the acknowledgement is not treated as the final reply for that turn
