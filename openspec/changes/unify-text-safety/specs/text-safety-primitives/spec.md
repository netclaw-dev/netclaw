## Purpose

Give every Netclaw feature that checks for bidirectional controls, Unicode
line separators, or the byte-order mark one shared definition of each set, and
give session storage one naming rule, so that two features cannot disagree by
accident.

Terms used in this capability:

- **Control characters**: the characters for which .NET `char.IsControl`
  returns true.
- **Bidirectional control characters**: U+061C, U+200E, U+200F,
  U+202A through U+202E, and U+2066 through U+2069.
- **Unicode line separators**: U+2028 and U+2029.
- **Byte-order mark**: U+FEFF.

## ADDED Requirements

### Requirement: One definition for each shared character set

Every feature that tests for bidirectional control characters, Unicode line
separators, or the byte-order mark SHALL use the definitions in this
capability. A feature MAY combine sets. A feature SHALL NOT define its own
member list for any of these sets.

#### Scenario: The same character is classified the same way

- **WHEN** approval display, the shell policy decision trace, approval entry validation, and log sinks each examine U+202E
- **THEN** each feature treats U+202E as a bidirectional control character

### Requirement: Approval prompt display escapes presentation controls

When an approval prompt shows a command or argument, the system SHALL escape
each control character, bidirectional control character, Unicode line
separator, byte-order mark, and backtick as `\uXXXX`.

#### Scenario: A right-to-left override in a command

- **WHEN** an approval prompt shows a command that contains U+202E
- **THEN** the prompt shows `‮` in place of the character

#### Scenario: A line separator in an argument

- **WHEN** an approval prompt shows an argument that contains U+2028
- **THEN** the prompt shows ` ` and the argument stays on one line

### Requirement: The shell policy decision trace escapes unsafe text

The shell policy decision trace SHALL escape each control character,
bidirectional control character, Unicode line separator, and byte-order mark
as `\uXXXX`.

#### Scenario: A carriage return in a traced command

- **WHEN** the trace records a command that contains CR and LF
- **THEN** the trace field contains `\u000D\u000A`

### Requirement: Approval entry validation rejects control and bidi characters

When the system loads or saves an approval entry, it SHALL reject a field that
contains a control character or a bidirectional control character. Whitespace
rules stay specific to each field.

#### Scenario: A bidi override in an approval entry

- **WHEN** the system loads an approval entry whose pattern contains U+202E
- **THEN** the system rejects the entry with a prohibited-character error

#### Scenario: A Unicode line separator in a field that allows whitespace

- **GIVEN** an approval entry field that allows whitespace
- **WHEN** the field contains U+2028 and no control or bidi character
- **THEN** the system accepts the field

### Requirement: Session storage names follow one rule

The system SHALL build each session folder name and each session storage path
segment with one rule. The rule SHALL keep each letter, digit, and hyphen, and
SHALL replace every other character with an underscore.

#### Scenario: A session folder name

- **WHEN** the system builds the session folder name for the session ID `C0A/123.4`
- **THEN** the folder name is `C0A_123_4`

### Requirement: Existing session names stay stable across the upgrade

The session storage naming rule SHALL produce the same output as the previous
release for every input. A session folder that exists before the upgrade SHALL
resolve to the same folder after it.

#### Scenario: An existing session folder after the upgrade

- **GIVEN** a session folder that the previous release created
- **WHEN** the upgraded daemon resolves that session
- **THEN** it finds the same folder, with no move or copy
