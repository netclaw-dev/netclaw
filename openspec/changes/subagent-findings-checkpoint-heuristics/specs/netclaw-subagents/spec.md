## MODIFIED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that
execute an autonomous LLM tool loop and return a single text result plus an
optional structured findings envelope. A subagent SHALL stop itself after
completing its task. Subagents SHALL NOT persist durable memory, stream direct
durable-memory writes, or participate in session pub/sub by default. Only
subagents whose definitions explicitly allow findings emission SHALL return a
structured findings envelope; all other subagents SHALL return text output only.

#### Scenario: Subagent completes with text response and optional findings

- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes its LLM/tool loop and returns a `SubAgentResult`
- **AND** the result MAY include structured findings only when that subagent is
  configured to emit findings
- **AND** the subagent stops itself

#### Scenario: Subagent executes tool calls in a loop

- **GIVEN** the LLM returns `FunctionCallContent` tool calls
- **WHEN** the subagent processes the response
- **THEN** it executes the tool calls via `DispatchingToolExecutor`
- **AND** sends tool results back to the LLM
- **AND** continues until the LLM returns a text response

#### Scenario: Subagent hits maximum tool iterations

- **GIVEN** the subagent has executed 10 tool iterations
- **WHEN** the LLM returns another tool call
- **THEN** the subagent forces a final LLM call with tools omitted
- **AND** returns the resulting text response

#### Scenario: Default subagent cannot write durable memory directly

- **GIVEN** a default subagent is executing within a user-facing session
- **WHEN** it attempts to persist durable cross-session memory directly
- **THEN** the durable write path is unavailable or denied to that subagent
- **AND** the subagent must return findings to the parent session instead

#### Scenario: Non-findings-capable subagent returns no findings envelope

- **GIVEN** a subagent definition does not explicitly allow findings emission
- **WHEN** that subagent completes successfully
- **THEN** the `SubAgentResult` contains only text output
- **AND** no structured findings envelope is returned for checkpoint review

## ADDED Requirements

### Requirement: Findings-capable subagent envelope review contract

A findings-capable subagent SHALL return information that may deserve durable
memory as a structured findings envelope to the owning session. Findings
candidates in the envelope SHALL represent durable
conclusions rather than raw work logs, step-by-step execution trace, or
unfiltered tool transcripts. Each candidate SHALL include enough metadata for
parent-session review, including suggested `domain`, `sensitivity`,
`confidence`, `durability`, and `reusability`, plus provenance or evidence
references where available. The owning session SHALL evaluate each findings
candidate as `accept`, `defer`, or `reject`, convert only accepted findings into
checkpoints, and remain the default durable-memory owner.

#### Scenario: Findings-capable subagent returns durable conclusion candidates

- **GIVEN** a findings-capable subagent completes research work
- **WHEN** it includes a structured findings envelope in `SubAgentResult`
- **THEN** the envelope contains conclusion-level candidates suitable for parent
  review
- **AND** the envelope includes the required review metadata for each candidate

#### Scenario: Parent session accepts findings for checkpoint review

- **GIVEN** a subagent returns findings that include stable project information
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the parent session converts only the accepted findings into durable
  memory checkpoints
- **AND** background curation proceeds under the parent session's policy scope

#### Scenario: Parent session defers ambiguous findings

- **GIVEN** a subagent returns a findings candidate with incomplete metadata or
  uncertain durability
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the candidate is marked `defer`
- **AND** no durable memory write occurs in MVP-now

#### Scenario: Parent session rejects raw work-log findings

- **GIVEN** a findings-capable subagent returns step-by-step execution notes or
  raw tool transcript content as a findings candidate
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the candidate is rejected as not durable-memory eligible
- **AND** no durable checkpoint is created from that candidate

#### Scenario: Parent session rejects findings on policy grounds

- **GIVEN** a subagent returns findings whose domain or sensitivity violates the
  parent session's durable-memory policy
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the findings are rejected or kept transient only
- **AND** no durable memory write occurs
