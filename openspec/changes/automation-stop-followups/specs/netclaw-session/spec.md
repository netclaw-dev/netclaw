## ADDED Requirements

### Requirement: A person ends an automation turn

An automation turn is a turn whose requester has the principal `VerifiedAutomation` or `SystemProcess`. Examples are a
reminder fire, a background job result, a webhook, and an approval notice. A waiting person is a buffered input with
the principal `Operator` or `TrustedInternal` that is not a child result notice. See
[the engineering glossary](../../../../../docs/spec/GLOSSARY.md) for the requester.

The session SHALL end a running automation turn at a safe boundary when a person waits. There are two safe boundaries:
the end of a tool batch, and the end of a compaction that the tool loop started. At a safe boundary the session SHALL
apply these rules in this order:

- A restart drain request wins. The existing restart path stops the turn, and the person's turn does not start.
- When a person waits, no buffered input joins the automation turn, and the automation turn SHALL make no further model
  call. The session SHALL close the selected inputs of the turn and complete the turn quietly.
- When the end is at a tool batch and a compaction is due at that batch, the session SHALL commit the compaction
  after the close and before the first model call of the person's turn.
- Each waiting input SHALL then start its own turn under its own recorded authority, in admission order.

A stop SHALL NOT close an input that no model request carried.

The first model request of the person's turn SHALL hold this note once, in a per-turn system context message after the
history of the stopped turn and before the person's message:

`The automated turn above stopped unfinished because a person sent a message. Answer the person's message first, and do not resume the stopped work in this reply unless the person asks for it.`

The note SHALL be fixed text. It SHALL NOT hold a source kind, a sender, or other text that an input sets. Later
requests of the person's turn SHALL keep that history entry and SHALL NOT add a second note.

The session SHALL NOT end a turn for an `UntrustedExternal` input, a child result notice, or an input of the same
requester. A person SHALL NOT end the turn of another person. An overflow replay is the current turn's own message, so
it SHALL continue its turn.

Owners: the input flow owns the stop decision, and its data is actor-local (the buffer and the current authority). The
session actor owns the close, the compaction, and the phase change; the journal keeps the close and the compaction.
The model context owns the note; the pending note is actor-local until the actor puts it into history.

```text
schematic: safe boundary of an automation turn (omits the ACL and provider steps)
  if restart requested: existing restart drain; stop
  if an Operator or TrustedInternal person waits (not a child result notice):
    close the selected inputs; complete the turn quietly
    if at a tool-batch end and compaction is due: commit the compaction
    drain in FIFO order; each input adopts its recorded authority
    first request of the person's turn: insert the stop note once, before the person's message
  else: continue the tool loop, or compact first when it is due
```

#### Scenario: A person ends a reminder turn at a tool-batch end

- **GIVEN** a reminder turn runs a tool loop
- **AND** the operator's message waits in the buffer
- **WHEN** the current tool batch ends
- **THEN** the reminder turn makes no further model call and completes quietly
- **AND** the operator's message starts its own turn under the operator's authority
- **AND** the first request of that turn holds the stop note once, before the operator's message

#### Scenario: A due compaction runs before the person's first model call

- **GIVEN** a reminder turn reaches a tool-batch end with the compaction threshold reached
- **AND** the operator's message waits in the buffer
- **WHEN** the reminder turn ends
- **THEN** the session commits the compaction before the first model request of the operator's turn
- **AND** that request holds the stop note once, before the operator's message

#### Scenario: A person who writes during a tool-loop compaction ends the turn

- **GIVEN** a reminder turn reached the compaction threshold at a tool-batch end and the compaction runs
- **WHEN** the operator writes before the compaction ends
- **THEN** the reminder turn makes no model call after the compaction and completes quietly
- **AND** the operator's message starts its own turn, and its first request holds the stop note once

#### Scenario: A restart request wins over the person

- **GIVEN** a person waits behind a reminder turn
- **WHEN** a restart drain request is active at the tool-batch end or the compaction end
- **THEN** the session follows the restart path and starts no model call for the person

#### Scenario: An untrusted person does not end an automation turn

- **GIVEN** a reminder turn runs a tool loop
- **WHEN** an `UntrustedExternal` input waits at the tool-batch end or the compaction end
- **THEN** the reminder turn continues its tool loop
- **AND** the input waits for its own turn, and no request holds the stop note

#### Scenario: A person does not end the turn of another person

- **GIVEN** Alice's turn runs a tool loop
- **WHEN** Bob's input waits at the tool-batch end or the compaction end
- **THEN** Alice's turn continues and Bob's input runs after it, with no stop note

#### Scenario: An overflow replay keeps its turn

- **GIVEN** a reminder turn overflowed the context window and its message waits as a replay
- **AND** the operator's message waits behind it
- **WHEN** the overflow compaction ends
- **THEN** the replay continues the reminder turn, and the operator's message waits

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

Compaction logic SHALL be encapsulated in a `SessionCompactionPipeline` static
utility class, separate from the session actor. The pipeline accepts a
`SessionState` snapshot and `CompactionParameters` record and sends results
back to the actor via `self.Tell()`.

Each compaction SHALL run tool-result clearance and then the extractive
reduction. The clearance SHALL NOT skip the reduction. The trigger compares the
input token count that the provider reports with the threshold, and the
pipeline has only an estimate. A skip on the estimate could leave the history
over the threshold and start a new compaction at each turn end.

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

- **GIVEN** `UsageDetails.InputTokenCount` exceeds the compaction token limit
  derived from `ModelCapabilities.CompactionTokenLimit(SessionTuning.CompactionThreshold)`
- **WHEN** compaction runs
- **THEN** the actor enters `Compacting` phase via `TransitionTo(Compacting)`
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
  (configurable via `SessionTuning.KeepRecentToolResults`)
- **AND** the extractive reduction runs next, also when the clearance alone
  brings the estimate under the threshold

#### Scenario: Tiered compaction — structured summarization

- **GIVEN** phase 1 (tool clearing) completed
- **WHEN** the extractive reduction discards messages and the observer LLM call runs
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
