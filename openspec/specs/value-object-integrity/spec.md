# value-object-integrity Specification

## Purpose

Establish the cross-cutting invariant that identifier and trust-label fields
crossing actor boundaries are represented as validating value-object types
rather than raw primitives. Value objects validate their input at
construction, expose no implicit conversion to or from the primitive, provide
named factories for known constants, and preserve the underlying wire/disk
format through serializer mapping — making the type system itself the gate
against passing a wrong-but-same-typed value where a domain-specific identifier
or label is expected.

## Requirements

### Requirement: Identifier and trust-label fields are value-typed

Records that cross an actor boundary SHALL represent identifier and
trust-label fields — trust boundary, sender id, agent name, model id, turn
number, and the already-wrapped tool-call id, tool name, session id,
background-job id, and reminder id — as value-object types rather than raw
`string`, `int`, or `long` primitives. The compiler SHALL reject supplying a
value object of one domain where another domain's type is expected.

#### Scenario: Mismatched identifier fails to compile

- **WHEN** code supplies a `SenderId` where a `SessionId` parameter is expected
- **THEN** the build fails with a type-mismatch error
- **AND** no run-time conversion bridges the two types

#### Scenario: A wrapped field carries its value unchanged

- **WHEN** a protocol record is constructed with a value-object field and the
  field is later read
- **THEN** the value object's `Value` equals the primitive originally supplied

### Requirement: Value objects validate and do not implicitly decay

A value object that has a defined validity rule SHALL reject null, empty, or
malformed input at construction by throwing an explicit exception. A value
object SHALL NOT expose an implicit conversion to or from its underlying
primitive; conversion SHALL be explicit — `Value` access or an explicit cast.

#### Scenario: Invalid input rejected at construction

- **WHEN** a value object with a non-empty validity rule is constructed from
  empty or whitespace input
- **THEN** construction throws an explicit exception
- **AND** no instance carrying the invalid value is produced

#### Scenario: Primitive access is explicit

- **WHEN** code needs the underlying primitive of a value object
- **THEN** it reads `Value` or applies an explicit cast
- **AND** no implicit conversion to the primitive compiles

### Requirement: Trust boundary is a value object with named constants

The trust boundary partition label SHALL be represented by a `TrustBoundary`
value object. The well-known boundaries — public, personal, team, and
trusted-instance — SHALL be exposed as named static factories. Code SHALL NOT
assign or compare a trust boundary using a bare string literal.

#### Scenario: Well-known boundary obtained from a named factory

- **WHEN** code needs the personal trust boundary
- **THEN** it uses the `TrustBoundary.Personal` factory
- **AND** no magic string literal is written at the callsite

#### Scenario: Boundary equality is value-based

- **GIVEN** two `TrustBoundary` values constructed from the same canonical label
- **WHEN** they are compared
- **THEN** they are equal

### Requirement: Value objects preserve wire and disk format

A value object SHALL serialize as its underlying primitive whenever it crosses
the protobuf serialization boundary or is persisted to a JSON document. The
on-wire and on-disk byte representation SHALL be identical to the
pre-value-object representation, so that a daemon running a build from before
the value-object change and one running a build from after can read each
other's persisted journal entries and documents.

#### Scenario: Protobuf round-trip is byte-identical

- **GIVEN** a protobuf-registered type whose field was wrapped in a value object
- **WHEN** an instance is serialized and then deserialized
- **THEN** the serialized bytes are identical to the pre-wrap serialization of
  the same logical value
- **AND** the deserialized value object equals the original

#### Scenario: Persisted JSON document round-trips unchanged

- **GIVEN** a persisted record — a background job or reminder definition — with
  a value-object field
- **WHEN** it is written to disk and re-read
- **THEN** the JSON document stores the bare primitive, not a nested object
- **AND** the value is read back verbatim
