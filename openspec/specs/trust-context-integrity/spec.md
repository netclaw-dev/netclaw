# trust-context-integrity Specification

## Purpose

Establish the cross-cutting invariant that trust-bearing context — audience,
principal, boundary, provenance, transport authenticity, and payload taint — is
mandatory and non-optional at every actor boundary. No security-relevant field
may carry a permissive or elevated sentinel default; missing trust context
fails loud rather than silently defaulting. This capability makes the type
system itself the primary correctness gate against silent trust-context
fallbacks.
## Requirements
### Requirement: Trust context is mandatory at actor boundaries

Every record that carries trust context across an actor boundary SHALL declare
its trust-bearing fields — audience, principal, boundary, provenance, transport
authenticity, and payload taint — as non-optional. A trust-bearing field SHALL
NOT be nullable and SHALL NOT carry a sentinel default value. The compiler
SHALL reject construction of such a record that omits any trust-bearing field.

#### Scenario: Omitting a trust field fails to compile

- **WHEN** code constructs a trust-bearing record without supplying every
  trust-bearing field
- **THEN** the build fails with a missing-required-member error
- **AND** no permissive or elevated value is substituted

#### Scenario: Trust-bearing record carries explicit values

- **WHEN** a trust-bearing record is constructed
- **THEN** every trust-bearing field holds a value explicitly supplied by the
  caller
- **AND** no field was populated by a framework-supplied default

### Requirement: No permissive or elevated defaults on security-relevant fields

A security-relevant field SHALL NOT be assigned a permissive default (a value
granting broader trust than the caller intended) or an elevated default (a
value granting narrower-but-higher-privilege trust such as `Personal`) when its
source value is absent. When trust context is genuinely required but absent,
the system SHALL fail loudly rather than substitute any default.

#### Scenario: Missing turn source fails loud

- **GIVEN** a code path that requires a turn source to derive trust context
- **WHEN** the turn source is absent
- **THEN** the system throws an explicit error identifying the missing context
- **AND** the operation does not proceed with a substituted audience or
  boundary

#### Scenario: Conservative fallback only where partial absence is normal

- **GIVEN** a derivation path where the absence of a source is a defined,
  normal condition
- **WHEN** the source is absent
- **THEN** the system MAY substitute a documented fail-closed value (the most
  restrictive trust level)
- **AND** the system SHALL NOT substitute a value more permissive or more
  privileged than fail-closed

### Requirement: Parsed trust types instead of wire strings

Trust context carried into tool execution SHALL be represented as parsed,
strongly-typed values. An audience SHALL be a parsed `TrustAudience`, not an
unvalidated wire string. A value that cannot be parsed SHALL fail at the point
of construction, not at the point of a later authorization check.

#### Scenario: Unparseable audience fails at construction

- **WHEN** trust context is built from an audience value that cannot be parsed
- **THEN** construction throws an explicit parse error
- **AND** the failure occurs before any tool authorization check runs

#### Scenario: Tool authorization reads a parsed audience

- **WHEN** a tool authorization check reads the execution audience
- **THEN** the audience is already a parsed `TrustAudience`
- **AND** the check performs no string parsing and applies no parse-failure
  fallback

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

