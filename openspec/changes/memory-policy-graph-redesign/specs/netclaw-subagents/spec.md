## MODIFIED Requirements

### Requirement: Subagent execution contract

The system SHALL run subagents as ephemeral actors (`SubAgentActor`) that execute an autonomous LLM tool loop and return a single text result plus an optional structured findings envelope. A subagent SHALL stop itself after completing its task. Subagents SHALL NOT persist durable memory, stream direct durable-memory writes, or participate in session pub/sub by default.

#### Scenario: Subagent completes with text response and findings
- **GIVEN** a `SubAgentDefinition` with a name, system prompt, and tool list
- **WHEN** the subagent receives a `RunSubAgent` message
- **THEN** the subagent executes its LLM/tool loop and returns a `SubAgentResult`
- **AND** the result MAY include structured findings for the parent session to review
- **AND** the subagent stops itself

#### Scenario: Default subagent cannot write durable memory directly
- **GIVEN** a default subagent is executing within a user-facing session
- **WHEN** it attempts to persist durable cross-session memory directly
- **THEN** the durable write path is unavailable or denied to that subagent
- **AND** the subagent must return findings to the parent session instead

## ADDED Requirements

### Requirement: Subagent findings handoff to owning session

When a subagent discovers information that may deserve durable memory, it SHALL return that information as a structured findings envelope to the owning session. The owning session SHALL evaluate policy, convert accepted findings into checkpoints, and remain the default durable-memory owner.

#### Scenario: Parent session accepts findings for checkpoint review
- **GIVEN** a subagent returns findings that include stable project information
- **WHEN** the parent session evaluates the subagent result
- **THEN** the parent session converts the accepted findings into a durable memory checkpoint
- **AND** background curation proceeds under the parent session's policy scope

#### Scenario: Parent session rejects findings on policy grounds
- **GIVEN** a subagent returns findings whose domain or sensitivity violates the parent session's durable-memory policy
- **WHEN** the parent session evaluates the findings envelope
- **THEN** the findings are dropped or kept transient only
- **AND** no durable memory write occurs
