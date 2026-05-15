## ADDED Requirements

### Requirement: Persisted reminder definitions carry required trust fields

A persisted `ReminderDefinition` SHALL declare its audience and boundary fields
as `required` and non-optional, so that every in-process construction is
enforced by the compiler. A legacy reminder JSON document that lacks these
fields SHALL be rejected at load — the reminder store SHALL log an error naming
the document and the missing fields, SHALL exclude the reminder from `Get` and
`List` (so it is never scheduled), and SHALL preserve the file on disk. The
system SHALL NOT substitute an audience or boundary for a reminder with no
persisted trust context.

#### Scenario: Legacy reminder document is rejected at load

- **GIVEN** a persisted `ReminderDefinition` JSON document that predates this
  change and lacks an audience or boundary field
- **WHEN** the reminder store reads it
- **THEN** the reminder is excluded — `Get` returns nothing and `List` omits it
- **AND** an error naming the document and the missing fields is logged
- **AND** the file is preserved on disk for the operator to repair or remove
- **AND** no audience or boundary is substituted, so the reminder is not scheduled

#### Scenario: Current reminder documents round-trip unchanged

- **GIVEN** a `ReminderDefinition` written after this change with explicit
  audience and boundary
- **WHEN** the reminder store deserializes it
- **THEN** the audience and boundary are read verbatim with no error logged
