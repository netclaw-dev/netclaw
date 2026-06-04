## ADDED Requirements

### Requirement: Session turn context is the durable authority model

The session actor SHALL distinguish ephemeral transport metadata from durable turn authority. `MessageSource` SHALL remain the transport/input shape for live delivery details. Session approval pause, recovery redrive, continuation tool execution, and memory-safety decisions SHALL use an explicit turn context as the durable authority model.

#### Scenario: Live turn builds explicit context

- **GIVEN** a `SendUserMessage` is accepted with a valid `MessageSource`
- **WHEN** the session starts processing the turn
- **THEN** the session builds an explicit turn context
- **AND** security-relevant turn decisions read from that context rather than directly from transport metadata

#### Scenario: Recovered turn does not synthesize transport authority

- **GIVEN** a session recovers an approval-paused turn without live transport metadata
- **WHEN** the approval response redrives the parked tool batch
- **THEN** the session uses the persisted turn context as authority
- **AND** it does not synthesize a `MessageSource` as the authority source for the recovered turn

### Requirement: Missing required turn context fails loud

When a session path requires turn context to authorize tool execution, expose tools, or evaluate memory safety, missing or incomplete turn context SHALL fail loudly. The system SHALL NOT silently substitute a permissive or unrelated context. A documented fail-closed compatibility path MAY exist only for legacy events where partial absence is expected and safe.

#### Scenario: Missing context blocks redrive

- **GIVEN** a recovered pending approval has no complete turn context and cannot be safely restored from legacy persisted fields
- **WHEN** the user approves the prompt
- **THEN** the session does not redrive the tool under a substitute context
- **AND** the failure is logged or surfaced explicitly

#### Scenario: Tool execution reads parsed trust values

- **GIVEN** a tool call is dispatched from a live or recovered session turn
- **WHEN** the tool execution context is built
- **THEN** the audience and boundary come from parsed turn-context values
- **AND** tool authorization does not parse unvalidated wire strings at the point of use

### Requirement: Shared context model excludes actor lifecycle state

Any context model shared between session and sub-agent approval work SHALL contain only execution authority and provenance fields whose semantics are the same in both actors. Actor-specific lifecycle state, including session journal recovery state and sub-agent watchdog/approval-wait state, SHALL remain separate.

#### Scenario: Shared model carries authority only

- **GIVEN** session approval recovery and sub-agent approval lifecycle both need requester, audience, boundary, channel type, and approval capability
- **WHEN** a shared model is introduced
- **THEN** it contains those authority fields
- **AND** it does not contain session-only redrive state or sub-agent-only watchdog state
