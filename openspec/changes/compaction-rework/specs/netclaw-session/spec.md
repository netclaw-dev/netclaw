# netclaw-session Delta Spec — compaction-rework

## MODIFIED Requirements

### Requirement: Conversation compaction

The system SHALL compact long session history using a tiered approach that
produces a structured summary surviving successive compactions without
grounding decay, enforces tool call/result pair integrity at the compaction
boundary, and disambiguates the self session from any foreign session
identifiers referenced in the discarded window. Before and after compaction
boundaries, the session SHALL emit high-priority memory checkpoints into the
durable memory queue instead of performing a synchronous one-off memory flush
that depends on the turn path completing all curation work inline.

The compaction observer LLM SHALL produce output in a fixed structured
format with nine sections: Primary Request and Intent, Key Technical
Concepts, Files and Code Sections, Problem Solving, Pending Tasks, Task
Evolution, Current Work, Next Step, and Required Files. The Task Evolution
section SHALL contain direct quotes from user messages that changed the
task, to prevent drift across successive compactions.

The compaction summary message SHALL be wrapped with a distinctive header
of the form `[session-summary session:{id}]` so that consumers (the
observer on successive compactions, the reducer, and the UI) can
recognize it as a prior-compaction artifact and preserve it across
successive compactions without relying on a separately-persisted index.

The compaction observer SHALL receive the self `SessionId` in its system
prompt and SHALL explicitly mark any foreign session identifiers in
observations as `session:{id}` rather than conflating them with the self
session.

The compaction observer system prompt SHALL include a rule instructing the
model to preserve any prior structured summary block verbatim and update
in place, rather than re-summarizing or rewriting it.

#### Scenario: Compaction threshold reached

- **GIVEN** `UsageDetails.InputTokenCount` exceeds `SessionConfig.CompactionTokenLimit`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` behavior state
- **AND** incoming messages are buffered during compaction

#### Scenario: Compaction boundary emits memory checkpoint

- **GIVEN** compaction is about to run or has just completed a summary reduction
- **WHEN** the compaction boundary is reached
- **THEN** the session enqueues a high-priority memory checkpoint for durable
  curation
- **AND** the user-facing session does not wait for background curation to
  finish

#### Scenario: Tiered compaction — tool result clearing first

- **GIVEN** compaction is triggered
- **WHEN** phase 1 runs
- **THEN** old tool results are replaced with placeholders
- **AND** the N most recent tool interactions are preserved in full
- **AND** if threshold is now satisfied, no summarization LLM call is made

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) did not bring context under threshold
- **WHEN** the observer LLM call runs
- **THEN** the observer produces a summary containing the nine fixed sections
  (Primary Request and Intent, Key Technical Concepts, Files and Code Sections,
  Problem Solving, Pending Tasks, Task Evolution, Current Work, Next Step,
  Required Files)
- **AND** the Task Evolution section contains direct quotes from user
  messages that changed the task
- **AND** the summary is wrapped with a `[session-summary session:{id}]`
  header and stored in the compacted history
- **AND** a `SessionCompacted` event is persisted carrying the compacted
  messages
- **AND** a persistence snapshot is taken
- **AND** compacted state remains usable for future turns

#### Scenario: Successive compactions do not re-summarize prior summary

- **GIVEN** a session that has been compacted, with a prior
  `[session-summary session:{id}]` message in history
- **WHEN** a subsequent compaction is triggered
- **THEN** the observer system prompt instructs the model to preserve the
  prior summary block verbatim and update its sections in place
- **AND** the reducer's user-message-boundary walk-back preserves the
  prior summary message in the kept window (the summary is a User-role
  message with a distinctive header)

#### Scenario: Self session disambiguation in observer

- **GIVEN** the discarded window contains a reference to a session identifier
  that is not the running session (e.g. the agent was investigating another
  session via a tool call)
- **WHEN** the observer LLM call runs
- **THEN** the observer system prompt includes the self session id
- **AND** the produced summary marks the foreign session as `session:{id}`
- **AND** the produced summary does not conflate the foreign session with the
  self session

#### Scenario: Tool call/result pair integrity during compaction

- **GIVEN** conversation history contains tool call/result pairs
- **WHEN** the extractive reducer selects the kept window
- **THEN** the kept window starts on a `User`-role message (not a
  `Tool`-role message and not an `Assistant` message that contains
  `FunctionCallContent` without a matching preceding user turn)
- **AND** tool call/result pairs are never split across the compaction
  boundary
- **AND** older tool interactions remain representable in the journal for
  checkpoint extraction and summarization
