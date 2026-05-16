## ADDED Requirements

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
