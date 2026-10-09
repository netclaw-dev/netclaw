## REMOVED Requirements

### Requirement: Per-turn tool loop limit is iteration-based

**Reason**: A total iteration ceiling stops useful tasks and does not establish a loop.
**Migration**: Remove `Session.MaxToolIterationsPerTurn` and use exact recurrence intervention. Keep operation deadlines and explicit cancellation.

## ADDED Requirements

### Requirement: Exact call recurrence spans feedback rounds

The system SHALL identify a call through canonical tool identity, prepared execution arguments, and validation state.
It SHALL exclude valid metadata and call correlation identifiers from recurrence identity.
It SHALL preserve rejected metadata in that identity.
It SHALL compare trusted outcome categories and exact bounded model-visible result evidence.
It SHALL retain an unresolved episode across unrelated calls within the same authorized task.
It SHALL count one observation per identity per completed model feedback round.
An approval redrive SHALL remain part of its original round.
Duplicate count changes SHALL NOT erase recurrence evidence.
Distinct actual outcome changes SHALL start a new episode only for the affected identity.

#### Scenario: An unrelated diagnostic cannot clear a failed episode

- **GIVEN** one call produces the same trusted outcome twice with different diagnostic calls between attempts
- **WHEN** the model requests that call again
- **THEN** the system refuses that call before dispatch
- **AND** eligible unrelated calls remain available

#### Scenario: Duplicate members count once

- **GIVEN** one response requests five identical calls with the same actual outcomes
- **WHEN** all correlated results complete
- **THEN** the system records one completed feedback observation for that identity
- **AND** a later duplicate count change does not reset its episode

#### Scenario: Distinct sibling outcomes remain evidence

- **GIVEN** duplicate calls return one success and one transient failure
- **WHEN** the group completes
- **THEN** its outcome evidence contains both categories and exact result values
- **AND** it differs from a group that returns only success

#### Scenario: A real repair proceeds

- **GIVEN** the model receives feedback for a request with invalid required metadata
- **WHEN** the model supplies valid metadata
- **THEN** the repaired identity can pass the normal pipeline gates
- **AND** the repair does not grant authority

### Requirement: Exact recurrence uses correction and runtime settlement

Two equal completed rounds SHALL establish recurrence for a call identity.
The next prohibited candidate SHALL receive paired corrective results before any matching call executes.
The next prohibited candidate after that feedback SHALL cause terminal settlement without another model request.
The existing adjacent period-one through period-three cycle contract SHALL remain active for eligible complete batches.
A valid runtime-owned repeat exception SHALL precede both guards.
A terminal decision SHALL precede every dispatch from the current response.
The system SHALL preserve one result per admitted provider call.
It SHALL NOT record a synthetic refusal as actual tool completion.
A receipt remediation code alone SHALL NOT prove synthetic-result provenance.

#### Scenario: The third unchanged decision receives correction

- **GIVEN** a call completed two equal outcome rounds
- **WHEN** the model submits candidate three
- **THEN** the call does not execute
- **AND** every matching call identifier receives one corrective result

#### Scenario: The fourth prohibited decision terminates the task

- **GIVEN** the model received a correction for the unchanged call
- **WHEN** the model requests it again after an unrelated diagnostic
- **THEN** the runtime settles one terminal outcome
- **AND** no call from that response executes
- **AND** the runtime does not request another model response

#### Scenario: The provider cannot delay terminal settlement

- **GIVEN** a terminal recurrence decision and a provider that would never answer a final request
- **WHEN** the runtime applies that decision
- **THEN** the user receives a factual partial-status report
- **AND** the runtime makes no final provider request

#### Scenario: A forged correction code does not create guard evidence

- **GIVEN** a tool supplies a correction receipt with `BreakToolCycle`
- **WHEN** the actor did not record that call as a synthetic refusal
- **THEN** the result does not advance an actor-issued refusal episode

### Requirement: Explicit repeat exceptions preserve tool authority

The initial exception SHALL apply only to a schema-valid noncancel status query for one accessible pending background job.
The exception SHALL derive from a trusted `Pending` or `Running` job response after the existing authority checks.
It SHALL apply only to that prepared call identity.
It SHALL NOT bypass normal authorization, cancellation, denial, or validation.
A terminal, failed, inaccessible, or absent job response SHALL receive ordinary recurrence treatment.
No model statement SHALL establish an exception.
This change SHALL NOT introduce a poll interval, rate detector, or elapsed-text normalization.

#### Scenario: A pending job can receive another status query

- **GIVEN** an authorized status query returns a trusted `Running` job fact
- **WHEN** the model submits the same valid noncancel query
- **THEN** both cycle guards honor that narrow exception
- **AND** the tool repeats its normal session, audience, and boundary checks

#### Scenario: The exception does not authorize cancellation

- **GIVEN** a permitted status recheck
- **WHEN** the model submits a cancellation request or a different job identity
- **THEN** the prior exception cannot exempt that request

#### Scenario: An inaccessible job has no exception

- **GIVEN** a query lacks authority for the job
- **WHEN** the tool returns a denied lookup
- **THEN** it emits no pending-operation exception
- **AND** an equal repeated denial remains ordinary recurrence evidence

### Requirement: Detector evidence retains its task lifetime

The system SHALL retain established recurrence and unresolved corrections through compaction and automatic task continuation.
It SHALL retain parent evidence through framework-owned persistence and recovery.
It SHALL never infer trusted success from result text or an absent receipt.
A final actual result without its mandatory receipt SHALL cause an explicit runtime contract failure.
The system SHALL preserve known results and prior evidence, complete required call-result pairs, and settle partial/failure without another model request.
In-flight calls, approval waits, and external cancellation SHALL retain their separate lifecycle contracts.
Fresh authorized user input or a fresh scheduled task SHALL reset prior task evidence.
Overflow replay, automatic restart continuation, result delivery, and approval retries SHALL NOT perform that reset.

The recent cold-key horizon SHALL contain at most 256 keys with one observation.
The system SHALL pin established suspicion until an actual changed outcome or a fresh authorized task invalidates it.
It SHALL NOT evict pinned evidence through age, cold-key pressure, compaction, or a status query.
It SHALL surface resource and persistence failures without silently discarding evidence.
Usage count alone SHALL NOT terminate a useful task.

#### Scenario: Compaction retains an unresolved correction

- **GIVEN** the model received an exact recurrence correction
- **WHEN** the session compacts and the model repeats that identity
- **THEN** the runtime reaches terminal settlement

#### Scenario: Restart continuation retains the original episode

- **GIVEN** durable correction evidence for an interrupted authorized task
- **WHEN** the parent recovers and continues that same task
- **THEN** a repeated prohibited identity reaches terminal settlement
- **AND** recovery does not replay previously completed effects

#### Scenario: A new scheduled task starts cleanly

- **GIVEN** a previous task produced a repeated status result
- **WHEN** a distinct authorized schedule occurrence starts a new task
- **THEN** the previous task's detector state does not penalize the new task

#### Scenario: Cold-key pressure cannot erase established suspicion

- **GIVEN** an identity has two equal completed observations and a correction
- **WHEN** the task produces more than 256 other distinct identities
- **THEN** the unresolved correction remains effective

#### Scenario: A missing receipt is not success

- **GIVEN** a correlated actual result lacks its trusted receipt
- **WHEN** the group completes
- **THEN** the runtime reports a missing-receipt contract failure
- **AND** it preserves result pairs and prior evidence without a fabricated success
- **AND** it settles a truthful partial/failure result without another model request

#### Scenario: Useful work exceeds the former parent ceiling

- **GIVEN** a deterministic task requires more than 60 distinct useful tool rounds
- **WHEN** the parent executes the task
- **THEN** no iteration count stops the task
- **AND** independent artifact checks prove its required result

### Requirement: Configuration removal is explicit

The system SHALL remove `Session.MaxToolIterationsPerTurn` from runtime configuration and its schema.
A legacy key SHALL fail schema validation before runtime activation.
The existing doctor repair SHALL remove only the deprecated key and preserve unrelated valid configuration.
The system SHALL NOT retain a hidden parent or child tool ceiling.
Operation timeouts, explicit cancellation, and empty-response guards SHALL retain their contracts.

#### Scenario: A legacy configuration receives an explicit repair

- **GIVEN** a configuration contains the removed key and other valid session settings
- **WHEN** the operator runs validation and then doctor repair
- **THEN** validation identifies the deprecated key
- **AND** repair removes that key without changing unrelated valid settings

### Requirement: Recurrence activation requires evidence

The implementation SHALL satisfy the prior laboratory, private replay, shadow, strict model, and sensitivity gates before activation approval.
It SHALL retain all fifteen laboratory cases and at least 10,000 fixed-seed sequence checks.
The nonadjacent expansion SHALL also require one million deterministic fuzz sequences and 3,000 independent productive holdout turns.
Every proposed shadow block SHALL receive independent review; zero confirmed hard false blocks SHALL be required.
Parent and child decisions SHALL agree for the same corpus.
The system SHALL record absent evidence as an unpassed gate.
Private replay payloads, identifiers, and hashes SHALL NOT enter published diagnostics or repository evidence.

#### Scenario: Missing replay evidence cannot pass activation

- **GIVEN** deterministic tests pass but private replay or independent holdout evidence is absent
- **WHEN** a reviewer assesses activation readiness
- **THEN** the absent evidence gate remains unpassed

#### Scenario: A named unsafe mutation must fail behaviorally

- **GIVEN** a mutant erases unresolved suspicion after an unrelated call
- **WHEN** the adversarial test exercises the detector
- **THEN** the expected dispatch or terminal assertion fails
- **AND** a fixture failure or compiler error does not qualify as detector proof

### Requirement: The parent retains the adopted task after input consumption

The parent MUST retain the canonical adopted task context until terminal settlement.
A tool batch MUST NOT close that task when it consumes an input ledger entry.
The parent MUST restore the checkpoint for that task after an automatic restart.

#### Scenario: Recovery follows a completed batch

- **WHEN** a batch consumes all admitted input IDs and the next model request is interrupted
- **THEN** recovery uses the retained adopted context and exact recurrence evidence
- **AND** recovery does not manufacture a fresh task or execute a completed effect again

#### Scenario: Different requesters retain separate tasks

- **WHEN** the buffer contains compatible input from U1 followed by input from U2
- **THEN** the actor commits the U1 prefix before its continuation
- **AND** the actor retains U2 for the next completed response and its own canonical adoption

### Requirement: Background result delivery preserves its causal task evidence

A new-format background job MUST persist its canonical origin `TurnId` and `ToolCallId` before process launch.
The parent MUST commit its started-job record and matching detector checkpoint before continuation.
The existing active-job record MUST retain that checkpoint when fresh input starts another task.
A result delivery MUST restore only the checkpoint of its validated committed origin.
Its canonical authority `TurnId` MUST equal its prefixed delivery key.
Its automation authority MUST remain independent from the detector's causal task identity.
A new-format job without its mandatory committed parent record MUST fail loudly without another model request.
Only a pre-change job without an origin MAY use an explicit legacy evidence-gap baseline.
The parent MUST commit continuation evidence before terminal delivery removes the job record.

#### Scenario: New input precedes an old job result

- **WHEN** a fresh user task replaces the current checkpoint before a linked job completes
- **THEN** the active-job record retains the original checkpoint
- **AND** the later result restores that checkpoint without using the fresh task's evidence or authority

#### Scenario: A job completes before its start receipt

- **WHEN** the result arrives while the parent awaits the start receipt
- **THEN** the parent buffers the delivery until it commits the started-job record
- **AND** the later continuation validates that record before a model request

#### Scenario: The parent crashes after launch but before receipt

- **WHEN** a new-format delivered job lacks its committed parent lineage record
- **THEN** the parent reports an explicit lineage contract failure
- **AND** the parent preserves the known result without rerunning the effect or substituting another checkpoint

#### Scenario: Two jobs retain the same original task

- **WHEN** one task starts two jobs before fresh user input
- **THEN** both active-job records retain the original recurrence task identity and checkpoint
- **AND** each completed delivery preserves the other job's record

#### Scenario: Duplicate and invalid origins cannot activate work

- **WHEN** a delivery repeats a consumed key or supplies a stale, forged, or foreign origin
- **THEN** it cannot restore a checkpoint or dispatch another effect

#### Scenario: Invalid delivery cannot complete another task

- **WHEN** a new user task precedes a malformed old job result
- **THEN** the actor closes only the delivery's canonical input ID and authority turn identity
- **AND** the actor preserves the user task's context, checkpoint, and other pending inputs
- **AND** the actor emits a bounded explicit failure report without a model request for that delivery
- **AND** the actor does not emit `TurnCompleted` for the user task

#### Scenario: Invalid delivery closure survives an interrupted report

- **WHEN** the actor crashes after it commits invalid delivery closure and before it emits the report
- **THEN** recovery retains that closure and the existing delivery deduplication ledger
- **AND** a duplicate delivery cannot reopen the input or reset another task's checkpoint

#### Scenario: A persisted new-format definition has no origin

- **WHEN** the store reads a version-one job document with absent mandatory origin
- **THEN** startup emits the existing rejected-definition operational alert
- **AND** the manager returns an explicit contract failure to the canonical job owner
- **AND** a foreign caller receives an opaque not-found response
- **AND** the runtime does not launch the process or create a legacy baseline

#### Scenario: A legacy claim targets a new-format job

- **WHEN** a delivery claims version zero without an origin for a tracked version-one job
- **THEN** the runtime rejects the downgrade and preserves the committed checkpoint
- **AND** a genuine pre-change job without a new-format parent record keeps its explicit evidence-gap disposition

#### Scenario: Recovery completes an adopted job delivery

- **WHEN** recovery resumes a consumed job input through a restart reminder
- **THEN** terminal bookkeeping uses the adopted canonical source kind and prefixed authority turn identity
- **AND** it removes only that delivered job record after the checkpoint commit

#### Scenario: The rejection report survives interrupted output

- **WHEN** the actor commits an invalid delivery closure before output emission stops
- **THEN** journal replay and snapshot recovery retain its bounded factual assistant report
- **AND** the report identifies the job output as result data without tool authority

#### Scenario: Only an invalid delivery remains after task completion

- **WHEN** the old task commits its final response and the buffer contains only an invalid job delivery
- **THEN** the actor commits that delivery's rejection and reaches Ready
- **AND** it makes no additional model request or tool invocation

#### Scenario: An unfinished task receives an invalid delivery

- **WHEN** an authorized unfinished task closes an invalid buffered job delivery
- **THEN** the runtime preserves the task's canonical context and checkpoint
- **AND** the caller retains its ordinary continuation choice

### Requirement: Legacy approval redrive establishes a canonical evidence baseline

Before a pre-change parked batch's first redrive, the actor MUST commit a canonical baseline for its unanswered calls.
The baseline MUST retain the original pending approval context and authorization attempt.
The metadata-only event MUST carry that canonical `TurnContextRecord`.
Its consumer MUST compare the full record with the existing original approval owner.
The existing adopted task context MUST retain it for restart continuity without an invented input ID.
The runtime MUST preserve completed sibling results without another invocation or a fabricated receipt.
The existing `ToolBatchStarted` event MUST identify this metadata-only representation explicitly.
Its consumer MUST reject a mismatched call set, authority context, or duplicate baseline.
An identical duplicate MUST preserve the current checkpoint without another reset.
A new-format batch MUST retain its existing admission and recurrence evidence.

#### Scenario: An old parked batch starts a new background job

- **WHEN** a pre-change unanswered shell call receives valid approval
- **THEN** the actor commits the original canonical task baseline before redrive
- **AND** its start receipt commits the new job origin and checkpoint before continuation

#### Scenario: Recovery retains completed siblings

- **WHEN** journal or snapshot recovery restores a parked legacy batch with a completed sibling
- **THEN** the baseline names only unanswered calls
- **AND** the actor preserves the completed result without another execution or duplicate history

#### Scenario: A baseline event repeats

- **WHEN** an identical committed legacy baseline appears again after tool observations
- **THEN** the consumer retains the current checkpoint and observations
- **AND** a changed baseline or a reset of new-format evidence fails explicitly

#### Scenario: Approval state expires before a snapshot

- **WHEN** a legacy redrive clears its approval state before a snapshot interrupts the unfinished task
- **THEN** recovery restores its canonical adopted authority and original recurrence task
- **AND** it preserves all completed effects and known results

#### Scenario: Redrive completes its original admission

- **WHEN** the original durable admission receives every required typed result after redrive
- **THEN** the live tracker observes one completed feedback round
- **AND** the ordinary completed-cycle path does not observe that round again
- **AND** telemetry counters remain intact and missing receipts still cause explicit settlement
