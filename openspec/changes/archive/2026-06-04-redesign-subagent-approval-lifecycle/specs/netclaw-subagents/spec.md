## ADDED Requirements

### Requirement: Sub-agent approval lifecycle is actor-local

Sub-agent approval waits SHALL be owned by the live `SubAgentActor` run that encountered the approval-gated tool call. The sub-agent SHALL NOT persist approval wait state or reuse the session approval recovery/redrive lifecycle from `LlmSessionActor`.

#### Scenario: Approval wait belongs to live child actor
- **GIVEN** a sub-agent tool call requires approval
- **WHEN** the sub-agent enters an approval wait
- **THEN** the wait is tracked by the live `SubAgentActor`
- **AND** no sub-agent approval wait state is written to the session journal

#### Scenario: Parent stop cancels sub-agent approval wait
- **GIVEN** a sub-agent is waiting for parent approval
- **WHEN** the parent session stops or cancels the `spawn_agent` tool call
- **THEN** the sub-agent approval wait is cancelled
- **AND** the sub-agent completes at most once with a failed `SubAgentResult`
- **AND** the gated tool is not executed after cancellation

#### Scenario: Parent session recovery expires live-only prompt
- **GIVEN** a sub-agent is waiting for parent approval
- **WHEN** the parent session cold-recovers before the user responds
- **THEN** the sub-agent approval prompt has no durable redrive state
- **AND** a later approval response is rejected as expired

### Requirement: Sub-agent approval uses parent turn authority

Sub-agent approval prompts SHALL use the parent session turn's execution authority context for approval requester, principal, audience, boundary, channel capability, provenance, adopted-context safety, and filesystem grounding. The implementation SHALL reuse the `TurnContext` or shared execution-authority subset from #1213 when available, and SHALL keep any interim field mapping isolated to the parent-to-child spawn boundary.

#### Scenario: Approval prompt carries parent requester context
- **GIVEN** a sub-agent spawned from a parent turn with a requester sender id and principal
- **WHEN** the sub-agent emits an approval prompt
- **THEN** the prompt carries the parent requester sender id and principal
- **AND** approval authorization is evaluated as if the parent turn had requested the tool

#### Scenario: Missing authority fails closed
- **GIVEN** a sub-agent approval-gated tool call has no parent approval bridge or required authority context
- **WHEN** approval is required
- **THEN** the gated tool is not executed
- **AND** the sub-agent completes with a failed `SubAgentResult`
- **AND** no default `Personal` audience or synthetic requester is substituted

#### Scenario: Human approval requires requester binding
- **GIVEN** a sub-agent approval-gated tool call has a parent approval bridge
- **AND** the parent turn is not verified automation
- **AND** the parent turn has no requester sender identity or no requester principal
- **WHEN** approval is required
- **THEN** no approval prompt is emitted
- **AND** the sub-agent completes with a failed `SubAgentResult`

### Requirement: Sub-agent watchdog pauses during human approval

The sub-agent inactivity watchdog SHALL treat parent approval waits as intentional suspension. While one or more approval waits are active, watchdog timeout ticks SHALL NOT complete the sub-agent as inactive. When the last approval wait settles, the watchdog SHALL be re-baselined so future inactivity is still bounded.

#### Scenario: Slow approval does not trigger inactivity timeout
- **GIVEN** a sub-agent with an active approval wait
- **AND** the human approval decision takes longer than the sub-agent inactivity budget
- **WHEN** the approval eventually arrives
- **THEN** the sub-agent applies the approval outcome
- **AND** the sub-agent is not failed for inactivity during the wait

#### Scenario: Parent spawn-agent watchdog pauses during approval
- **GIVEN** a parent session is consuming a streaming `spawn_agent` tool call
- **AND** the child sub-agent is waiting for human approval longer than the parent tool inactivity budget
- **WHEN** the approval wait is still active
- **THEN** the parent `spawn_agent` tool call is not timed out for inactivity
- **AND** the parent tool watchdog resumes after the approval wait settles

#### Scenario: Parallel approval waits keep watchdog paused until all settle
- **GIVEN** a sub-agent tool batch with two approval-gated calls
- **WHEN** both calls are waiting for parent approval
- **THEN** the watchdog remains paused until both approval waits have settled
- **AND** the watchdog is re-armed only after the final wait completes

### Requirement: Sub-agent approval outcomes settle exactly once

Each sub-agent approval-gated tool call SHALL settle exactly once as approved, denied, timed out, or cancelled. Approved decisions SHALL retry only the blocked call with retry-local approval state. Denied and timed-out decisions SHALL become tool-result messages visible to the sub-agent LLM. Cancellation and actor termination SHALL not produce duplicate `SubAgentResult` messages.

#### Scenario: Approve once is retry-local
- **GIVEN** a sub-agent approval-gated tool call is approved once
- **WHEN** the sub-agent retries the blocked call
- **THEN** the retry-local approval applies only to that tool call
- **AND** sibling calls, later tool iterations, and later sub-agent runs still require approval when policy requires it

#### Scenario: Denied approval becomes tool result
- **GIVEN** a sub-agent approval-gated tool call is denied by the user
- **WHEN** the approval decision is delivered
- **THEN** the tool is not executed
- **AND** the sub-agent receives a tool-result message explaining that approval was denied
- **AND** the sub-agent may continue or finish within the normal tool-iteration limit

#### Scenario: Timed-out approval becomes tool result
- **GIVEN** a sub-agent approval-gated tool call receives an expired or timed-out approval decision
- **WHEN** the decision is delivered to the sub-agent
- **THEN** the tool is not executed
- **AND** the sub-agent receives a tool-result message explaining that approval timed out

#### Scenario: Terminal races complete once
- **GIVEN** a sub-agent has an in-flight approval wait
- **WHEN** cancellation, timeout, and approval completion messages race
- **THEN** the sub-agent sends at most one `SubAgentResult` to the caller
- **AND** the first terminal path wins
