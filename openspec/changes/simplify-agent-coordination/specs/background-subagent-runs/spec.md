## Purpose

This capability lets a parent accept, supervise, and receive child work across process restarts without repeating uncertain child effects.

## ADDED Requirements

### Requirement: Durable child acceptance precedes execution

The system SHALL validate a child request and its effective authority before it records acceptance. It SHALL record acceptance before it starts child work. `spawn_agent` and routed skill activation SHALL return the accepted run identity after that durable acceptance and before child completion.

The same accepted start identity SHALL NOT start a second child. A retry with the same identity and different payload SHALL fail as a conflict. A distinct authorized start SHALL remain distinct even when its task text matches another task. Task text SHALL NOT serve as run identity.

#### Scenario: Accepted start returns before child completion

- **GIVEN** a valid child request whose acceptance write succeeds
- **WHEN** the parent invokes `spawn_agent` or routed skill activation
- **THEN** the parent receives the accepted run identity before child completion
- **AND** the child starts only after acceptance persists

#### Scenario: Failed acceptance starts no child

- **GIVEN** a valid child request whose acceptance write fails
- **WHEN** the parent invokes the child
- **THEN** the request returns a failure
- **AND** no child action starts

#### Scenario: Repeated accepted identity does not start another child

- **GIVEN** a child start has durable acceptance
- **WHEN** the same start identity is received again
- **THEN** the system returns the existing accepted run
- **AND** it starts no second child

#### Scenario: Conflicting retry does not reuse an accepted identity

- **GIVEN** a child start identity already has durable acceptance
- **WHEN** the system receives that identity with a different payload
- **THEN** the system returns an explicit conflict
- **AND** it starts no child for the conflicting payload

### Requirement: Child execution keeps the parent's authority

The system SHALL preserve the original requester, audience, and authority boundary for child tools, approvals, status, and cancellation. A child result SHALL NOT grant authority. A child worker SHALL remain ephemeral and SHALL NOT restart an uncertain external action after owner recovery. Status SHALL distinguish durable acceptance from terminal completion. The run owner's status response SHALL include the pending approval count. Other requesters SHALL NOT see that count. An approval for a stale or unrelated run, call, requester, or attempt SHALL grant no authority.

The system SHALL persist cancellation intent before it acknowledges the intent. That acknowledgement SHALL NOT mean that the run is complete. The system SHALL report terminal cancellation only after the local dispatch confirms closure, and it SHALL start no new dispatch after closure. An uncertain external effect SHALL remain explicit.

#### Scenario: Child approval keeps the original requester

- **GIVEN** a child tool requires human approval
- **WHEN** the parent or an authorized requester resolves that approval
- **THEN** the decision applies only to that child call and its recorded requester
- **AND** a stale or unrelated decision grants no authority

#### Scenario: Recovery does not repeat an uncertain child action

- **GIVEN** an accepted child has an external action with no recorded terminal result
- **WHEN** the parent owner recovers
- **THEN** the system does not start the child again automatically
- **AND** the run reports an explicit non-success or uncertain state

#### Scenario: Status enforces run ownership

- **GIVEN** one requester owns a child run
- **WHEN** another requester asks for its status
- **THEN** the system denies that status request
- **AND** the owner can observe recorded progress and pending approval facts

#### Scenario: Accepted status is not terminal status

- **GIVEN** a child run has durable acceptance but no terminal result
- **WHEN** its owner requests status
- **THEN** the system reports a nonterminal state
- **AND** it does not report child completion

#### Scenario: Pending approval count is owner-only

- **GIVEN** an owned child run has one pending approval
- **WHEN** its owner requests status
- **THEN** the response includes a pending approval count of one
- **WHEN** that approval settles and the owner requests status again
- **THEN** the response includes a pending approval count of zero
- **AND** a non-owner cannot observe that count

#### Scenario: Stale approval does not grant authority

- **GIVEN** an approval refers to a stale run, call, requester, or attempt
- **WHEN** the system receives that approval
- **THEN** it rejects the approval as stale
- **AND** it dispatches no action under that approval

#### Scenario: Cancellation reports closure after local dispatch stops

- **GIVEN** an owner requests cancellation of an active child run
- **WHEN** the system persists cancellation intent
- **THEN** it acknowledges the intent without reporting terminal cancellation
- **WHEN** the local dispatch confirms closure
- **THEN** the system can report terminal cancellation
- **AND** it starts no new child dispatch after closure
- **AND** an uncertain external effect remains explicit

### Requirement: Ready results resume the parent at a safe boundary

The system SHALL save a child's raw terminal result before asynchronous preparation. A preparation failure SHALL leave that result available for retry. The system SHALL make a result eligible for delivery when preparation completes.

The parent SHALL select a ready child result without ordering it by child completion time. It SHALL deliver that result at the next safe model boundary. It SHALL NOT interrupt an active model call or incomplete tool batch. Ordinary user inputs SHALL run in ordered batches of one requester, each under its own recorded authority.

The parent SHALL record result consumption with the correlated parent decision. It SHALL keep an unconsumed result available after recovery. Delivery SHALL use the canonical assistant/tool result pair with a fresh delivery call ID that differs from the initial acceptance call ID. Consumption SHALL retain the child's original authority for downstream tools and approval. The system SHALL NOT fabricate an execution receipt or successful result when no such result exists.

#### Scenario: A prepared result passes an earlier unready result

- **GIVEN** child A finishes first but its result is not ready
- **AND** child B's result is ready
- **WHEN** the parent reaches a safe model boundary
- **THEN** child B's result is eligible for delivery first
- **AND** the order does not depend on which child finished first

#### Scenario: A result waits for an active tool batch

- **GIVEN** a child result becomes ready while the parent has an incomplete tool batch
- **WHEN** the result becomes eligible for delivery
- **THEN** it does not interrupt that batch
- **AND** the parent can consume it at a later safe boundary

#### Scenario: Ordinary inputs keep their order

- **GIVEN** ordinary inputs A and B form an ordered parent batch
- **WHEN** a child result becomes ready
- **THEN** the child result does not reorder or replace inputs A and B

#### Scenario: Preparation failure keeps the raw result

- **GIVEN** a child has saved a terminal result
- **WHEN** result preparation fails
- **THEN** the raw result remains available
- **AND** the system can retry preparation without repeating child work

#### Scenario: Recovery keeps an unconsumed result

- **GIVEN** a child result is ready but no parent decision consumed it
- **WHEN** the parent recovers
- **THEN** the same result remains eligible for delivery
- **AND** the system does not repeat child execution

#### Scenario: Result delivery uses a new call identity

- **GIVEN** the parent accepted a child with an initial call identity
- **WHEN** the parent receives the child's terminal result
- **THEN** the parent receives a canonical assistant/tool result pair
- **AND** the delivery call ID differs from the initial acceptance call ID

#### Scenario: Consumption keeps original downstream authority

- **GIVEN** the parent consumes a child result
- **WHEN** the parent acts on that result or requests approval
- **THEN** downstream tools and approval use the original requester and authority boundary
- **AND** child result content grants no new authority

#### Scenario: Missing execution result stays explicit

- **GIVEN** the journal has no actual terminal execution result
- **WHEN** the parent prepares a child result for delivery
- **THEN** the system reports an explicit non-success or uncertain state
- **AND** it does not fabricate an execution receipt

### Requirement: Deployed child journal records remain recoverable

Before the system writes a new child journal format, it SHALL provide a tested read or migration path for deployed `tta-v1`, `cra-v1`, `cre-v1`, and extended existing records.

The path SHALL preserve accepted identities, terminal results, authority, pending consent, job lineage, and exactly-once transfer of delivered child results. It SHALL NOT execute an uncertain input or infer duplicate identity from matching task text. It SHALL preserve the original journal and a restore path until conversion succeeds.

#### Scenario: Valid deployed records migrate without lost facts

- **GIVEN** a valid deployed journal with old child and extended session records
- **WHEN** the system reads or migrates that journal
- **THEN** accepted identities, terminal results, authority, consent, lineage, and result transfer remain intact
- **AND** the converted journal replays with the candidate reader

#### Scenario: Invalid adoption receives a safe disposition

- **GIVEN** the reported journal contains an invalid task-adoption event
- **WHEN** the system migrates or recovers that journal
- **THEN** it does not execute the uncertain input
- **AND** it records an explicit safe disposition without comparing prompt text as identity
- **AND** it preserves the original journal and restore path

#### Scenario: New writes wait for compatibility proof

- **GIVEN** deployed child records lack a tested read or migration path
- **WHEN** the candidate starts a session with those records
- **THEN** it does not write the new child journal format to that session
- **AND** it reports the compatibility failure clearly

### Requirement: Each completed turn reports the open child runs

The session SHALL report its open child runs in every `TurnCompleted` output. This covers each outcome (`Completed`, `Failed`, and `Skipped`) and each emit site. A child run is open from durable acceptance until its result has had its follow-up parent turn. A run is closed when the parent records that turn. A failed follow-up turn also closes the run, because the session records the failure as the parent decision for that result and does not deliver it again.

The value SHALL come from session state after the turn event applies. The `TurnCompleted` of the follow-up turn SHALL NOT list the run that the turn consumed. The session SHALL publish the value on `TurnCompleted`. A client SHALL NOT need a direct ask to the session.

The wire form is `openChildRunIds`, an array of run IDs. A daemon SHALL write the array for every `TurnCompleted`, and it SHALL write an empty array when no run is open. A reader that finds the field absent or null reads "unknown" and uses an empty list. Use the [engineering glossary](../../../../../docs/spec/GLOSSARY.md) for the term "open child run".

Owner: `LlmSessionActor` assigns the value from `SessionState.ChildRuns`. The value is durable state, read at the time of the output. `SessionOutputDtoMapper` carries it.

The `TurnCompleted` type SHALL make the open runs a required property. A site that builds a `TurnCompleted` SHALL name the value, and the compiler SHALL reject a site that omits it. A default of an empty list would let a new site, or an edit that drops the line, end a headless run early with no signal. An empty list is a valid value only when the session has no open run. The mapper that reads a wire form without the field names the empty list itself.

Schematic example (it omits all other outputs):

```text
turn 1 completes: spawn_agent accepted run R1          -> TurnCompleted(1, open=[R1])
R1 finishes, its result is ready                       -> turn 2 starts at once
turn 2 completes: it consumed R1's result              -> TurnCompleted(2, open=[])
```

#### Scenario: A turn that starts a child reports it open

- **GIVEN** the parent calls `spawn_agent` in turn 1 and the child still runs
- **WHEN** turn 1 completes
- **THEN** its `TurnCompleted` lists the accepted run ID
- **AND** the `TurnCompleted` of the follow-up turn lists no run

#### Scenario: A child that finishes inside the turn still counts as open

- **GIVEN** a child finishes while turn 1 still runs
- **AND** the session starts turn 2 right after `TurnCompleted` of turn 1
- **WHEN** turn 1 completes
- **THEN** its `TurnCompleted` lists the run, because the parent has not yet had the follow-up turn
- **AND** a report that omitted the run would let a headless caller exit before turn 2

#### Scenario: Two ready results give two follow-up turns

- **GIVEN** two children have ready results
- **WHEN** turn 1, turn 2, and turn 3 complete in order
- **THEN** the lists hold two runs, then one run, then no run

#### Scenario: A failed turn still reports the open runs

- **GIVEN** turn 1 starts a child, and a provider error fails turn 1
- **WHEN** the failed `TurnCompleted` is emitted
- **THEN** it lists the open run

#### Scenario: A failed follow-up turn closes the run

- **GIVEN** the follow-up turn that delivers a child result fails
- **WHEN** the failed `TurnCompleted` is emitted
- **THEN** it lists no run, because the session closed that run and will not deliver the result again
- **AND** a client that waits for the run would otherwise wait for ever

#### Scenario: A skipped turn reports the open runs

- **GIVEN** a child run is open and the session rejects an input before a model call
- **WHEN** the skipped `TurnCompleted` is emitted
- **THEN** it lists the open run

#### Scenario: A rejected slash command reports the open runs

- **GIVEN** a child run is open
- **AND** the user sends an unknown slash command, so the session rejects it before a model call
- **WHEN** the skipped `TurnCompleted` is emitted
- **THEN** it lists the open run

#### Scenario: A routed skill turn reports the open runs

- **GIVEN** a child run from an earlier turn is open
- **AND** the user sends a slash command that routes to a subagent, and that routed run finishes
- **WHEN** the completed `TurnCompleted` of the routed turn is emitted
- **THEN** it lists the earlier open run, not an empty list

#### Scenario: The type requires the value

- **GIVEN** the `TurnCompleted` type
- **WHEN** a site builds a `TurnCompleted` without the open runs
- **THEN** the code does not compile

#### Scenario: A follow-up turn with its own tool batch emits the delivery pair once

- **GIVEN** the follow-up turn that delivers a child result calls a tool of its own
- **WHEN** the tool batch finishes and the next model call starts
- **THEN** subscribers saw exactly one `child_run_result` tool call and one tool result for that delivery
- **AND** the next model call does not emit the pair again

#### Scenario: A session with no child reports an empty list

- **GIVEN** a session that never started a child
- **WHEN** any turn completes
- **THEN** the `TurnCompleted` has an empty list
- **AND** the wire form holds an empty array, not an absent field

#### Scenario: An earlier daemon sends no field

- **GIVEN** a `TurnCompleted` DTO without `openChildRunIds`
- **WHEN** a client maps it
- **THEN** the mapped output has an empty list
- **AND** no error occurs

### Requirement: A delivered child result appears as tool output

When the session selects a ready child result for a model call, it SHALL emit one `ToolCallOutput` and one `ToolResultOutput` for the delivery pair. The tool name is `child_run_result`. The call ID is the delivery call ID, not the acceptance call ID. The session SHALL emit each delivery call ID once for the life of the session actor. The result text SHALL hold the child output. The model history SHALL NOT change.

#### Scenario: The delivery pair reaches subscribers once

- **GIVEN** a child result is ready and a subscriber uses `OutputFilter.Full`
- **WHEN** the parent turn that delivers the result runs
- **THEN** the subscriber gets one `ToolCallOutput` and one `ToolResultOutput` named `child_run_result`
- **AND** the result text holds the child output
- **AND** the call ID differs from the call ID of `spawn_agent`
