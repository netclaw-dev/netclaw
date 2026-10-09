## Purpose

Define durable acceptance, parent controls, later child results, cancellation, and explicit restart loss for background child tasks.
Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for shared terms.
Source requirements: PRD-001 FR-001, FR-002, FR-003, FR-005, FR-007, FR-011, and FR-017; PRD-008 scheduled skill execution.

## ADDED Requirements

### Requirement: All child starts return durable acceptance

Every explicit or routed child start SHALL return a machine-actionable accepted run identifier before the child completes.
Acceptance SHALL commit before child execution or the accepted response.
The response SHALL include the run ID, child scope ID, lifecycle state, and the deferred control tool name.
An accepted response SHALL NOT claim that the task succeeded or expose a final result before receipt.
Pre-admission failure SHALL return an explicit rejected start and create no child.
Post-admission failure SHALL retain the accepted identifier and produce a terminal failure for that run.

#### Scenario: Parent receives acceptance while the child remains held

- **GIVEN** an authorized child start and a child provider barrier
- **WHEN** the owner commits acceptance and the child reaches that barrier
- **THEN** the start call returns its accepted identifier before the barrier releases
- **AND** the parent can leave its tool phase and process later input

#### Scenario: Acceptance persistence fails

- **GIVEN** a prepared child start
- **WHEN** acceptance persistence fails
- **THEN** no child model or task tool executes
- **AND** the caller receives an explicit failure rather than a false accepted identifier

#### Scenario: Child creation fails after acceptance

- **GIVEN** acceptance commits before child creation
- **WHEN** child creation fails
- **THEN** the accepted run remains queryable
- **AND** one later terminal result describes the start failure

### Requirement: Tool acceptance has one canonical JSON representation

The tool acceptance body SHALL be one JSON object without a prose prefix, Markdown fence, or suffix.
The object SHALL contain `run_id`, `scope_id`, `state`, and `control_tool` as JSON strings.
Each required field SHALL occur exactly once.
The owner SHALL supply nonempty canonical run and scope identifier values from its committed acceptance record.
The identifier strings SHALL use `SubAgentRunId.Value` and `SubAgentScopeId.Value` from that record.
The first accepted response SHALL use `state: "Accepted"` and `control_tool: "check_agent_run"`.
That initial state SHALL describe the committed admission fact, rather than claim that the current live state cannot advance.
The adapter SHALL NOT derive identifiers or acceptance from child text, actor names, or an uncommitted proposal.
An equivalent start retry SHALL retain those identifiers and may report the owner's current recorded lifecycle state.
JSON field order and insignificant whitespace SHALL NOT change the contract.

`spawn_agent` and routed `skill_load` SHALL expose this body as their tool result string.
`ToolResultOutput.Result` and the corresponding model `FunctionResultContent.Result` string SHALL carry the same JSON object.
The adapter SHALL format that body once and reuse it for both consumers.
Direct slash adapters SHALL use the same committed owner values for their acceptance acknowledgement.
Their human prose may differ. Their identifiers, state, and control name SHALL NOT differ.
An acceptance body SHALL NOT contain a child's final output or claim successful task completion.
The later terminal result SHALL remain a distinct owner delivery under its fresh result correlation.

#### Scenario: A held child produces one accepted tool body

- **GIVEN** the owner commits acceptance and the child provider remains held
- **WHEN** the start adapter returns its tool result
- **THEN** the output event and model result contain the same JSON object
- **AND** its first state is `Accepted` with canonical nonempty owner identifiers and the exact control name
- **AND** the parent does not receive the child's final output as that acceptance

#### Scenario: A malformed or uncommitted body is not acceptance

- **GIVEN** a result has a missing, duplicate, non-string, or empty required field, or no committed owner acceptance
- **WHEN** a start consumer validates that result
- **THEN** it reports an invalid acceptance rather than a live accepted run
- **AND** it cannot infer acceptance or launch another child from human prose or child output
- **AND** a failed acceptance write still creates no child model request or task effect

### Requirement: Start retries preserve one durable run identity

The runtime SHALL stamp start correlation from the original admitted input and call context.
A tool start key SHALL contain the owner session ID, original admitted turn ID, and original tool call ID.
A slash start key SHALL contain the owner session ID, admitted input ID, original turn ID, and framework activation slot zero.
A scheduled slash start SHALL reuse the admitted scheduled input and its persisted occurrence correlation.
The model SHALL NOT supply these keys or mint a replacement key during retry.
Concurrent or recovered duplicates with the same key and canonical start digest SHALL return the same run.
A conflicting digest under an accepted key SHALL fail explicitly and SHALL NOT alter that run.

#### Scenario: A lost accepted response does not duplicate execution

- **GIVEN** the owner commits acceptance but the response never reaches the caller
- **WHEN** the runtime retries the same stamped start
- **THEN** it returns the existing run ID
- **AND** no second child or external task effect starts

#### Scenario: Conflicting retry fails without mutation

- **GIVEN** one key belongs to an accepted task
- **WHEN** a retry reuses that key with different task or resolved profile content
- **THEN** the runtime returns `start_conflict`
- **AND** the accepted task and its authority remain unchanged

#### Scenario: Two concurrent duplicates share acceptance

- **GIVEN** two equivalent start requests arrive before the acceptance write completes
- **WHEN** the write commits
- **THEN** both callers receive the same run ID
- **AND** exactly one child starts

### Requirement: The run lifetime is independent of its start call

After acceptance commits, the child SHALL use a run-owned lifetime.
Start-call completion, cancellation, or timeout SHALL NOT cancel that accepted run.
A later parent turn SHALL NOT replace the run's original authority or lifetime.
Explicit run cancellation, explicit parent stop, and owner loss SHALL end that lifetime through the defined terminal contract.
No task-lifetime time, token, or tool-count ceiling SHALL be introduced by this capability.
Existing per-operation health checks and the shared recurrence contract SHALL remain effective.

#### Scenario: Start-tool cancellation after acceptance leaves the child live

- **GIVEN** the owner committed a child acceptance
- **WHEN** the original start token cancels or its tool response times out
- **THEN** the child remains live until a run-level terminal boundary
- **AND** parent status and cancellation remain available

#### Scenario: A later user turn does not cancel a child

- **GIVEN** a live child that belongs to an earlier parent turn
- **WHEN** the parent accepts a later authorized user turn
- **THEN** the child retains its original context and lifetime
- **AND** the parent does not wait for that child's completion merely because it exists

### Requirement: Parent controls describe recorded state without widening authority

`check_agent_run(run_id, cancel=false)` SHALL return the owner's recorded run state.
The parent caller SHALL pass normal tool policy and match the owner session and original eligible requester context.
A child or mismatched session/requester SHALL receive a denial without target details.
The control SHALL NOT route through the shell-job manager or use shell authority merely because it resembles shell-job cancellation.
Status SHALL expose cancellation admission separately from confirmed local dispatch closure.
Repeated status or cancellation requests SHALL NOT create another child, terminal result, or model continuation.

#### Scenario: Original parent inspects its live run

- **GIVEN** the original authorized parent requester and its accepted run
- **WHEN** the parent loads and calls the control
- **THEN** it receives the run ID, recorded state, and cancellation/dispatch-closure facts
- **AND** the call does not wait for task completion

#### Scenario: Another session cannot inspect or cancel the run

- **GIVEN** another session supplies a valid run identifier
- **WHEN** it attempts status or cancellation
- **THEN** the runtime reveals no target state, paths, or requester details
- **AND** the target remains live

### Requirement: Cancellation has a durable admission and a confirmed local cutover

The owner SHALL persist cancellation admission before it reports `Cancelling`.
It SHALL then close normal task admission, cancel active requests, and settle run-owned approval prompts.
The closure SHALL include pending tool-dispatch workers and queued provider requests.
The runtime SHALL acknowledge dispatch closure only after no new model request, task tool, or approval retry can start.
An admission acknowledgement SHALL NOT claim that dispatch closure already occurred.
Already launched remote work that lacks a confirmed outcome SHALL remain explicitly unknown.

#### Scenario: Cancellation admission precedes closure

- **GIVEN** a live child with a held local dispatch worker
- **WHEN** cancellation admission commits before that worker acknowledges closure
- **THEN** status reports `Cancelling` with dispatch closure unconfirmed
- **AND** the runtime does not claim complete task termination

#### Scenario: No fresh dispatch follows closure acknowledgement

- **GIVEN** the runtime acknowledges local dispatch closure
- **WHEN** stale provider output or a late approval answer attempts more task work
- **THEN** no new tool, provider request, retry, or grant is created
- **AND** the recorded cancellation outcome remains effective

#### Scenario: Remote work ignores cancellation

- **GIVEN** a remote operation started before cancellation cutover
- **WHEN** that operation ignores cancellation and provides no final receipt
- **THEN** local settlement completes without waiting for remote cooperation
- **AND** the result identifies the unconfirmed external effect

### Requirement: Cancellation preserves recorded partial evidence through framework-only finalization

The runtime SHALL retain confirmed results and file activity during normal execution after each completed feedback round.
After local dispatch closure, finalization SHALL have a separate five-second grace deadline by default.
Finalization SHALL use a token independent of the cancelled task token.
It SHALL permit only framework-owned checkpoint preservation and atomic writes inside the assigned run artifact directory.
It SHALL NOT invoke a model, task tool, external request, project edit, or approval prompt action.
Grace expiry or report-write failure SHALL retain the last committed checkpoint and expose an explicit finalization reason.
The result SHALL distinguish useful partial evidence from successful task completion.

#### Scenario: Cancellation returns useful recorded evidence

- **GIVEN** a child completed useful file operations and recorded a checkpoint
- **WHEN** the parent cancels it
- **THEN** one cancelled terminal result retains that confirmed activity and valid artifact references
- **AND** no new model request occurs to produce the report

#### Scenario: Report write fails

- **GIVEN** cancellation finalization attempts an atomic run-local report write
- **WHEN** the write fails
- **THEN** the runtime returns retained checkpoint evidence with an explicit write-failure reason
- **AND** it does not expose a nonexistent report path

#### Scenario: Finalization does not finish within grace

- **GIVEN** a held framework finalization operation
- **WHEN** the five-second grace deadline expires
- **THEN** one cancelled terminal result uses the retained checkpoint
- **AND** late finalization cannot replace the terminal winner or start task work

### Requirement: Terminal precedence follows durable owner order

The owner SHALL accept terminal messages only from the recorded child generation.
The first committed terminal receipt SHALL win over later cancellation admission.
Cancellation admission that commits first SHALL prevent later success from becoming the terminal outcome.
The owner SHALL preserve safe evidence from a late child response without changing the cancelled outcome.
The child SHALL close task dispatch and retain its result until the owner acknowledges durable terminal receipt.
Duplicate terminal messages SHALL receive the same acknowledgement without another delivery admission.
Persistence failure SHALL NOT produce a successful terminal acknowledgement.

#### Scenario: Completion wins the durable race

- **GIVEN** a valid completion receipt commits before cancellation admission
- **WHEN** a later cancel request arrives
- **THEN** the completed outcome remains the terminal winner
- **AND** the cancel request does not create a second terminal result

#### Scenario: Cancellation wins the durable race

- **GIVEN** cancellation admission commits before a success receipt
- **WHEN** the late success arrives
- **THEN** the terminal outcome remains cancelled
- **AND** safe recorded evidence can remain available as partial evidence

#### Scenario: Terminal receipt persistence fails

- **GIVEN** a child waits for its terminal acknowledgement
- **WHEN** the owner's terminal persistence fails
- **THEN** no successful acknowledgement is emitted
- **AND** recovery or owner loss remains explicit rather than false delivery success

### Requirement: Cancelled results preserve wire compatibility without false success

A cancelled result SHALL keep `Success = false`, wire outcome `Failed`, and reason `CancelledByParent`.
Its recorded lifecycle state SHALL be `Cancelled`.
Its typed completion SHALL carry confirmed file activity without claiming successful completion.
The owner SHALL expose only validated run locations and safe result paths under ordinary path policy.
Child-authored path claims SHALL NOT supply access authority, an approval prompt answer, or unverified file activity.
A lost run SHALL have a failed wire outcome and an explicit owner-loss reason.

#### Scenario: Cancelled evidence does not become successful output

- **GIVEN** a cancelled child wrote a valid artifact before cutover
- **WHEN** the result is serialized and decoded
- **THEN** its cancellation state, failed wire outcome, and confirmed artifact survive
- **AND** no consumer interprets it as successful task completion

#### Scenario: A forged result path supplies no authority

- **GIVEN** a child result claims an unrelated protected path
- **WHEN** the owner validates the result and the parent attempts a read
- **THEN** the claim creates neither confirmed activity nor a path grant
- **AND** the ordinary read policy rejects unauthorized access

### Requirement: One durable terminal delivery admits one attributed continuation

Each run SHALL have one deterministic terminal delivery identifier.
The owner SHALL persist the terminal receipt before asynchronous enrichment.
It SHALL retain that receipt if enrichment fails and expose an explicit delivery warning.
Continuation admission SHALL atomically persist both its dedup marker and its input/transcript item.
Recovery SHALL restore an admitted unfinished continuation without admitting it again.
Completed continuation input SHALL NOT be reintroduced by duplicate result or recovery replay.
An absent persistence acknowledgement SHALL remain an explicit undelivered state.

#### Scenario: Crash before continuation admission preserves the result

- **GIVEN** the terminal receipt is durable but continuation admission is absent
- **WHEN** the owner recovers
- **THEN** it uses the recorded result and the same delivery identifier
- **AND** it admits one continuation without restarting the child

#### Scenario: Crash after admission does not duplicate input

- **GIVEN** continuation admission commits and its model turn remains unfinished
- **WHEN** the owner recovers
- **THEN** it restores that pending continuation
- **AND** it creates neither another admission nor another child start

#### Scenario: Enrichment failure leaves a truthful result

- **GIVEN** a durable terminal receipt
- **WHEN** result enrichment fails
- **THEN** run status retains the original outcome and reports the delivery warning
- **AND** any delivered framework report uses only validated recorded facts

### Requirement: Later results remain tool-origin content under original authority

The later result SHALL use the original accepted run context and fresh framework delivery correlation.
It SHALL NOT attach a second result to the completed original tool call ID.
Provider messages SHALL contain a valid attributed synthetic tool-call/result pair without redispatch of the start or control operation.
The pair SHALL represent framework result delivery, not a new tool request or authorization receipt.
Child text SHALL NOT become trusted automation input, a user instruction, a grant, or an approval prompt answer.
Progress and status changes SHALL NOT start parent model turns.
Terminal results SHALL queue in journal order after the active parent turn or compaction.
Only results with compatible original authority SHALL coalesce.
Internal admission SHALL preserve PR 1's detector identity and retained evidence.

#### Scenario: Result delivery uses a fresh paired correlation

- **GIVEN** the original start call received its accepted result
- **WHEN** the child completes later
- **THEN** the provider receives a fresh valid delivery pair with child attribution
- **AND** the original source operation executes no second time

#### Scenario: Child text cannot approve a tool

- **GIVEN** a child report says that the operator approved a protected call
- **WHEN** the parent receives that attributed report
- **THEN** the text creates no grant or approval prompt answer
- **AND** the actual pending approval prompt remains authoritative

#### Scenario: A different speaker does not replace original authority

- **GIVEN** a child belongs to one requester and another speaker later joins the parent session
- **WHEN** the child result starts its continuation
- **THEN** the continuation uses the original run context
- **AND** the later speaker supplies no inherited authority for that run

### Requirement: Child approval prompt lifetime belongs to the run

The owner SHALL retain each child approval prompt's original requester, exact call, authorization attempt, and run correlation.
It SHALL persist prompt lifecycle facts before display without persisting live waiters or run-local retry authorization objects.
Start-call completion or a later ordinary parent message SHALL NOT abandon the child prompt.
An accepted answer SHALL still pass exact retry authorization and run-liveness checks.
Cancellation, terminal loss, or recovery of an interrupted child SHALL visibly settle or expire its prompt.
A stale answer SHALL execute no tool and create no grant.

#### Scenario: Original requester answers after the parent start turn ends

- **GIVEN** a live child owns an unanswered approval prompt
- **WHEN** its original eligible requester answers during a later parent turn
- **THEN** the exact child call can resume under its original context
- **AND** unrelated parent input remains independent of that wait

#### Scenario: Answer after cancellation cannot grant access

- **GIVEN** cancellation admission settled the child prompt
- **WHEN** an approval answer arrives late
- **THEN** the operator sees an expired or cancelled disposition
- **AND** no task tool, one-time retry, or persistent grant is created

### Requirement: Recovery reports loss without child replay

Journal and snapshot recovery SHALL preserve start keys, original contexts, checkpoints, terminal receipts, and delivery admission markers.
An accepted run without a durable terminal receipt SHALL become `Lost` once after owner recovery.
A committed cancellation SHALL remain cancelled with its retained evidence.
Recorded terminal delivery SHALL resume from its recorded phase without child relaunch.
Recovered child prompts SHALL expire visibly.
Recovery SHALL NOT replay child tools, infer completion from text, or reconstruct child execution with the current requester.

#### Scenario: Accepted child becomes lost after abrupt restart

- **GIVEN** an accepted live child without a durable terminal receipt
- **WHEN** the owner recovers after abrupt restart
- **THEN** it records one explicit lost terminal outcome
- **AND** no replacement child or task side effect starts automatically

#### Scenario: Cancellation remains effective after restart

- **GIVEN** cancellation admission committed before restart
- **WHEN** the owner recovers without a child finalization response
- **THEN** it settles a cancelled result from retained evidence
- **AND** it cannot turn a stale success into the terminal outcome

### Requirement: Upgrade and rollback preserve explicit lifecycle limits

The candidate SHALL load captured pre-change journals and snapshots without fabricating accepted background runs.
It SHALL retain existing conversations, parent approval state, and session storage bindings.
Interrupted legacy synchronous calls SHALL use explicit loss/healing behavior rather than background relaunch.
Rollback SHALL either prove prior-reader compatibility for new records or require the pre-upgrade database backup.
No rollback procedure SHALL claim reversal of completed external effects.

#### Scenario: Captured old state remains readable

- **GIVEN** a pre-change snapshot and journal with parent approval and conversation state
- **WHEN** the candidate loads that captured state
- **THEN** it preserves those existing records and uses an explicit empty child ledger
- **AND** it infers no live child from old transcript text

#### Scenario: The previous binary cannot read a new record

- **GIVEN** a compatibility probe demonstrates that the previous binary rejects a new record
- **WHEN** rollback instructions are reviewed
- **THEN** they require the pre-upgrade database backup and active-run settlement
- **AND** they retain the warning that external effects remain

### Requirement: Responsiveness evidence reaches the provider boundary

The release SHALL prove a parent model response before release of a held child provider request under the selected supported capacity contract.
An actor acknowledgement alone SHALL NOT satisfy that model-response claim.
The capacity contract SHALL define admission outcomes and bounded queue behavior before dependent runtime implementation.
Status and cancellation SHALL remain locally usable under provider contention.
The runtime SHALL NOT silently add a provider scheduler, require unproven spare capacity, or weaken the response claim.

#### Scenario: Held child permits a parent model response

- **GIVEN** the selected supported provider configuration and a held child request
- **WHEN** the user submits a parent status question
- **THEN** the parent returns its model response before child release
- **AND** evidence records both provider requests and their barrier order

#### Scenario: Single-slot starvation remains an unpassed gate

- **GIVEN** a provider permits only the held child request
- **WHEN** no chosen contract supplies parent capacity
- **THEN** the model-response gate remains unpassed
- **AND** local actor control success cannot replace that evidence
