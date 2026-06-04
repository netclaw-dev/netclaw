## ADDED Requirements

### Requirement: Recovered pending approvals restore turn context

When a session recovers pending tool approvals from the journal, it SHALL also restore the original turn context for each pending approval. The restored context SHALL include the requester, audience, boundary, channel type, approval capability, principal classification, provenance, and adopted-context safety state needed to resume the original request faithfully.

#### Scenario: Pending approval restores original context

- **GIVEN** a `ToolApprovalRequested` event was written with turn context while a prompt was outstanding
- **WHEN** the session cold-recovers through the journal path
- **THEN** the pending approval is restored with that turn context
- **AND** approval redrive uses the restored context rather than deriving a new context from the session id

#### Scenario: Legacy approval event uses compatibility restoration

- **GIVEN** a pre-change `ToolApprovalRequested` event has no turn-context record but still has legacy persisted trust fields
- **WHEN** the session recovers the event
- **THEN** the session MAY construct turn context from those legacy fields
- **AND** this compatibility path is isolated from the normal new-event path

#### Scenario: Incomplete legacy approval fails loud

- **GIVEN** a recovered pending approval lacks enough persisted context to restore the original requester and trust context
- **WHEN** an approval response arrives for that pending call
- **THEN** the session does not redrive the tool under a synthesized permissive context
- **AND** the user receives an explicit expired or unrecoverable approval notice

### Requirement: Restarted approvals resume as the original request

An approval response received after idle passivation, cold recovery, or daemon restart SHALL resume the same original session turn. The resumed tool batch and any continuation LLM/tool calls SHALL use the restored turn context until the resumed turn completes or is abandoned.

#### Scenario: Approval after cold recovery resumes original request

- **GIVEN** a session has recovered a pending approval from the journal
- **WHEN** the original requester approves the prompt
- **THEN** the session re-drives the parked tool batch
- **AND** the tool execution context uses the original turn audience, boundary, channel type, and approval capability

#### Scenario: Continuation after redrive keeps restored context

- **GIVEN** a recovered approval redrive has completed its parked tool call
- **WHEN** the follow-up LLM response asks for another tool call in the same turn
- **THEN** the continuation tool call uses the same restored turn context
- **AND** it does not fall back to a context derived from missing transport metadata
