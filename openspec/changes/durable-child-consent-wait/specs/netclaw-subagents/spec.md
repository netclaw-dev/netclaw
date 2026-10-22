## MODIFIED Requirements

### Requirement: Sub-agent approval outcomes settle exactly once

Each sub-agent approval-gated tool call SHALL settle exactly once as approved, denied, or withdrawn. The call SHALL
settle when its run resumes, not inside a live wait. An approved call SHALL run only that call with retry-local
approval state. A denied or withdrawn call SHALL become a tool-result message visible to the sub-agent LLM and SHALL NOT
run. Cancellation and actor termination SHALL NOT produce duplicate `SubAgentResult` messages.

#### Scenario: Approve once is retry-local
- **GIVEN** a sub-agent approval-gated tool call is approved once
- **WHEN** the resumed sub-agent runs the parked call
- **THEN** the retry-local approval applies only to that tool call
- **AND** sibling calls, later tool iterations, and later sub-agent runs still require approval when policy requires it

#### Scenario: Denied approval becomes tool result
- **GIVEN** a sub-agent approval-gated tool call is denied by the user
- **WHEN** the run resumes
- **THEN** the tool is not executed
- **AND** the sub-agent receives a tool-result message explaining that approval was denied
- **AND** the sub-agent may continue or finish subject to recurrence governance

#### Scenario: Timed-out approval becomes tool result
- **GIVEN** a sub-agent approval-gated tool call whose prompt the session withdrew, for example because the channel
  could not show it
- **WHEN** the run resumes
- **THEN** the tool is not executed
- **AND** the sub-agent receives a tool-result message explaining that approval was not given
- **AND** no consent request expires on a timer

#### Scenario: Terminal races complete once
- **GIVEN** a sub-agent sends its park
- **WHEN** cancellation and the park acknowledgement race
- **THEN** the sub-agent sends at most one `SubAgentResult` to the caller
- **AND** the first terminal path wins

## REMOVED Requirements

### Requirement: Sub-agent watchdog pauses during human approval

**Reason**: A sub-agent no longer waits for a person in memory. It parks and stops, so no watchdog runs during the
wait. `spawn_agent` returns the accepted run at once, so no parent tool call waits either.
**Migration**: None. A parked run has no actor and no timer. Its prompt waits with no time limit, as the child consent
pause requires.
