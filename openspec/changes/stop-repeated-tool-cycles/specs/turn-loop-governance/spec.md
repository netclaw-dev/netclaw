## ADDED Requirements

The terms in this specification use the
[Netclaw engineering glossary](../../../../../docs/spec/GLOSSARY.md).

### Requirement: Tool batches have deterministic execution signatures

Each requested tool batch SHALL have one deterministic action signature.
Each completed batch SHALL have one deterministic iteration signature.

The action signature SHALL use the canonical tool name and the arguments sent
to execution. Netclaw SHALL remove its metadata before it computes the
signature. It SHALL exclude provider call identifiers.

Netclaw SHALL sort JSON object properties by ordinal name. It SHALL preserve
array order, identifiers, paths, cursors, user values, and duplicate batch
members. It SHALL sort batch members by canonical tool name and argument hash.

The iteration signature SHALL map each result to its request. It SHALL include
the receipt category and a hash of the exact bounded result text sent to the
model. Netclaw SHALL NOT infer a category from a text prefix.

#### Scenario: Metadata and object order do not change an action

- **GIVEN** two calls differ only by Netclaw metadata and JSON object property order
- **WHEN** Netclaw computes their action signatures
- **THEN** the signatures are equal

#### Scenario: An identifier or array order changes an action

- **GIVEN** two calls differ by an identifier value or JSON array order
- **WHEN** Netclaw computes their action signatures
- **THEN** the signatures differ

#### Scenario: A changed result changes an iteration

- **GIVEN** two equal actions return different bounded model-visible text
- **WHEN** Netclaw computes their completed iteration signatures
- **THEN** the iteration signatures differ

#### Scenario: A mixed parallel batch records complete outcomes

- **GIVEN** a parallel batch has one unchanged result and one changed result
- **WHEN** Netclaw computes its completed iteration signature
- **THEN** the complete signature differs from the prior batch
- **AND** one unchanged member does not define a cycle

### Requirement: Exact completed cycles stop the next execution

Netclaw SHALL retain at most six completed iteration signatures for the active
user turn. It SHALL check cycle periods one through three.

A cycle exists when the recent history ends with two equal copies of one
sequence. Netclaw SHALL block a candidate only when its action signature equals
the next action in that sequence.

Netclaw SHALL make this decision before authorization, approval, or tool
execution. It SHALL NOT record a cancelled or incomplete batch as a completed
iteration.

#### Scenario: Period-one cycle blocks a third execution

- **GIVEN** action `A` completed twice with equal outcomes
- **WHEN** the model requests action `A` again
- **THEN** Netclaw blocks the request before execution

#### Scenario: Period-two cycle blocks its next action

- **GIVEN** the completed history ends with `A, B, A, B`
- **WHEN** the model requests action `A`
- **THEN** Netclaw blocks the request before execution

#### Scenario: Period-three cycle blocks its next action

- **GIVEN** the completed history ends with `A, B, C, A, B, C`
- **WHEN** the model requests action `A`
- **THEN** Netclaw blocks the request before execution

#### Scenario: Corrected action executes

- **GIVEN** two completed calls returned equal validation failures
- **WHEN** the next call has corrected execution arguments
- **THEN** Netclaw executes the call through the normal policy path

#### Scenario: Changing poll result executes

- **GIVEN** repeated poll actions returned different model-visible results
- **WHEN** the model requests the poll again
- **THEN** Netclaw executes the poll

#### Scenario: Approval redrive is not a new candidate

- **GIVEN** a batch paused for approval after its cycle check
- **WHEN** an approval response redrives that same batch
- **THEN** Netclaw does not apply a second cycle check
- **AND** the approved batch keeps its normal authorization rules

### Requirement: Cycle intervention is paired and terminal on repetition

The first cycle block SHALL return one synthetic result for each requested
call. These results SHALL preserve call-result pair integrity and state that no
call executed.

The model SHALL receive one tool-enabled call after this correction. If the
model requests the same blocked action again, Netclaw SHALL force a text-only
response. It SHALL NOT execute the repeated action.

The text-only instruction SHALL require facts about completed and incomplete
work. It SHALL prohibit a success claim for the blocked operation.

#### Scenario: First block returns paired correction results

- **GIVEN** a candidate batch would continue a confirmed cycle
- **WHEN** Netclaw blocks the batch
- **THEN** each requested call receives one synthetic correction result
- **AND** the batch causes no requested side effect

#### Scenario: A different action remains available

- **GIVEN** Netclaw returned a cycle correction
- **WHEN** the model requests a different action
- **THEN** Netclaw applies the normal exposure, authorization, and approval rules

#### Scenario: A repeated blocked action forces completion

- **GIVEN** Netclaw returned a cycle correction for action `A`
- **WHEN** the model requests action `A` again
- **THEN** Netclaw disables tools for the next model call
- **AND** action `A` does not execute

### Requirement: Cycle state follows the active user turn

Netclaw SHALL keep cycle state actor-local and non-durable. A new user message
SHALL clear the completed history and the last blocked action.

Normal compaction and empty-response retries SHALL preserve cycle and text-only
stop state. Recovery SHALL start with empty cycle state.

Diagnostics SHALL include only the cycle period, repetition count, and
decision. They SHALL exclude arguments, results, session data, and hashes.

#### Scenario: Compaction preserves an active cycle

- **GIVEN** one complete cycle copy exists before normal compaction
- **WHEN** the same cycle completes after compaction
- **THEN** the next matching action is blocked

#### Scenario: A new user turn clears a prior block

- **GIVEN** the prior turn blocked action `A`
- **WHEN** a new user message starts a turn and requests action `A`
- **THEN** the prior block does not stop the new request

#### Scenario: Empty response does not re-enable tools

- **GIVEN** a repeated blocked action activated text-only completion
- **WHEN** the model returns an empty response and Netclaw retries
- **THEN** the retry still exposes no tools

#### Scenario: Cycle diagnostics contain no payload data

- **GIVEN** Netclaw detects or blocks a cycle
- **WHEN** it writes the diagnostic event
- **THEN** the event contains the period, repetition count, and decision only
- **AND** the event contains no arguments, results, session data, or hashes

## REMOVED Requirements

### Requirement: Per-turn tool loop limit is iteration-based

**Reason**: Exact cycle detection replaces the static limit after staged replay
and observe-only gates prove safe behavior.

**Migration**: Keep the current limits as temporary rollout guards. Remove the
parent configuration property, schema entry, and child constant after all gates pass.
