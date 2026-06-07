## ADDED Requirements

### Requirement: Interrupted tool history remains provider-valid

When a new user message interrupts an in-flight assistant tool-call batch, the
session SHALL preserve provider-valid history by recording tool results for any
assistant tool calls that would otherwise remain unanswered.

Synthetic results MAY state that the tool call was abandoned because a new user
message superseded the request. The session SHALL persist those synthetic
results before processing the interrupting user message as a fresh turn.

#### Scenario: Open tool calls are closed before fresh turn

- **GIVEN** session history ends with an assistant message containing tool calls
  that do not all have matching tool result messages
- **WHEN** a new real user message interrupts that tool batch
- **THEN** the session persists synthetic tool result messages for each
  unanswered tool call
- **AND** the interrupting user message is persisted or processed only after the
  open tool calls are closed

#### Scenario: Recovered interrupted history is valid

- **GIVEN** a process restart occurs after an interrupting user message has
  abandoned an in-flight tool batch
- **WHEN** the session recovers from the journal
- **THEN** no assistant tool-call message is recovered without matching tool
  result messages
- **AND** the session can process later turns without provider rejection caused
  by orphaned tool calls
