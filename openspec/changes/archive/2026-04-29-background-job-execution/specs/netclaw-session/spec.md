## MODIFIED Requirements

### Requirement: Persisted turn lifecycle

The system SHALL persist each completed turn and emit typed output events to
subscribers. Subscriber delivery SHALL use a direct subscription model with
`OutputFilter` bitmask so that subscribers control which output categories they
receive (Text, Thinking, ToolCalls, Usage). Lifecycle events (TurnCompleted,
ErrorOutput, SessionTitleOutput, ToolInteractionRequest) SHALL always be
delivered regardless of filter. `SubAgentOutput` events (Started/Completed
phases) SHALL be filtered under the `ToolCalls` category.

Multiple subscribers from different channels (e.g., Slack and TUI) SHALL
coexist on the same session actor. Each subscriber receives its own filtered
copy of output independently. Adding or removing a subscriber SHALL NOT affect
other active subscribers.

The session actor SHALL create an `IApprovalChannel` instance at session start
and pass it to the tool execution pipeline. During the Processing behavior
phase, the session actor SHALL handle `ToolInteractionResponse` messages by
completing the corresponding `TaskCompletionSource` in the approval channel.
The session actor SHALL also update the `CommandApprovalCache` based on the
approval decision (session-scoped for ApproveOnce, persistent via
`ToolApprovalStore` for ApproveAlways).

The persisted `TurnRecorded` event SHALL carry an optional
`SourceReminderId` field (protobuf tag 5, additive). When a
`SendUserMessage` arrives with `MessageSource.ReminderId` set, the
resulting `TurnRecorded` event SHALL copy that value into
`SourceReminderId` so that reminder-originated turns are distinguishable
in the journal (for forensics) and survive recovery (so the in-memory
dedup set can be rebuilt via event replay).

`SessionState` SHALL maintain an `ActiveBackgroundJobs` dictionary
(`ImmutableDictionary<string, ActiveJobInfo>`) persisted to the Akka journal.
`ActiveJobInfo` SHALL carry `JobId`, `Command`, `Rationale`, and `StartedAt`.
When a background job is started, the session SHALL persist an event adding
the job entry. When a background job result is delivered, the session SHALL
persist an event removing the job entry and adding the job ID to a dedup
set (mirroring `ProcessedReminderIds`). The working context SHALL surface
active background jobs with their rationales so the LLM knows what it is
waiting for after compaction or session resumption.

Background job completion delivered through `DeliverTrustedSessionTurn` SHALL
be treated as the trusted completion of the original tool execution, matching
the trust semantics of synchronous shell results. The session SHALL process the
delivery only within the originating session and the persisted originating
audience/boundary captured for that job.

#### Scenario: Persist and emit assistant reply

- **WHEN** the assistant produces a response
- **THEN** a `TurnRecorded` event is persisted
- **AND** typed output events are emitted to subscribers based on their filter

#### Scenario: Reminder-originated turn carries SourceReminderId

- **GIVEN** a `SendUserMessage` arrives with `MessageSource.ReminderId` set
- **WHEN** the turn completes and is persisted
- **THEN** `TurnRecorded.SourceReminderId` contains the reminder ID
- **AND** the reminder ID is added to `ProcessedReminderIds` for dedup

#### Scenario: Background job started persisted to session state

- **GIVEN** the pipeline routes a tool call to background execution
- **WHEN** `BackgroundJobStarted` is received by the session
- **THEN** an `ActiveJobInfo` entry is added to `ActiveBackgroundJobs`
- **AND** the addition is persisted to the journal

#### Scenario: Background job result delivery removes active job

- **GIVEN** a background job result arrives via `DeliverTrustedSessionTurn`
- **WHEN** the session processes the delivery
- **THEN** the job entry is removed from `ActiveBackgroundJobs`
- **AND** the job ID is added to the dedup set
- **AND** both changes are persisted to the journal

#### Scenario: Session applies trusted delivery with originating scope

- **GIVEN** a background job result arrives via `DeliverTrustedSessionTurn`
- **AND** the job has persisted originating audience/boundary metadata
- **WHEN** the session processes the delivery
- **THEN** the turn is treated with the same trust semantics as a synchronous
  shell result for that session
- **AND** processing remains scoped to the persisted originating
  audience/boundary

#### Scenario: Active jobs visible in working context

- **GIVEN** a session has active background jobs
- **WHEN** the working context is built for the LLM
- **THEN** the context includes a section listing pending jobs with their
  rationales and start times

#### Scenario: Active jobs survive session recovery

- **GIVEN** a session with active background jobs has been passivated
- **WHEN** the session rehydrates from the journal
- **THEN** `ActiveBackgroundJobs` is restored with all entries
- **AND** the background job dedup set is restored
