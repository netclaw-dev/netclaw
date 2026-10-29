## Purpose

Give every Netclaw feature that handles unsafe characters or builds names from
untrusted text one shared definition of each character set and each naming
rule, so that two features cannot disagree by accident.

Terms used in this capability:

- **C0 and C1 control characters**: the characters for which .NET
  `char.IsControl` returns true.
- **Bidirectional control characters**: U+061C, U+200E, U+200F,
  U+202A through U+202E, and U+2066 through U+2069.
- **Unicode line separators**: U+2028 and U+2029.
- **Byte-order mark**: U+FEFF.

## ADDED Requirements

### Requirement: One definition for each character set

Every feature that tests for C0 and C1 control characters, bidirectional
control characters, Unicode line separators, or the byte-order mark SHALL use
the definitions in this capability. A feature MAY combine sets. A feature SHALL
NOT define its own member list for any of these sets.

#### Scenario: The same character is classified the same way

- **WHEN** approval display, the shell policy decision trace, approval entry validation, and log sinks each examine U+202E
- **THEN** each feature treats U+202E as a bidirectional control character

### Requirement: Approval prompt display escapes presentation controls

When an approval prompt shows a command or argument, the system SHALL escape
each C0 or C1 control character, bidirectional control character, Unicode line
separator, byte-order mark, and backtick as `\uXXXX`.

#### Scenario: A right-to-left override in a command

- **WHEN** an approval prompt shows a command that contains U+202E
- **THEN** the prompt shows `‮` in place of the character

#### Scenario: A line separator in an argument

- **WHEN** an approval prompt shows an argument that contains U+2028
- **THEN** the prompt shows ` ` and the argument stays on one line

### Requirement: The shell policy decision trace escapes unsafe text

The shell policy decision trace SHALL escape each C0 or C1 control character,
bidirectional control character, Unicode line separator, and byte-order mark
as `\uXXXX`.

#### Scenario: A carriage return in a traced command

- **WHEN** the trace records a command that contains CR and LF
- **THEN** the trace field contains `\u000D\u000A`

### Requirement: Approval entry validation rejects control and bidi characters

When the system loads or saves an approval entry, it SHALL reject a field that
contains a C0 or C1 control character or a bidirectional control character.
Whitespace rules stay specific to each field.

#### Scenario: A bidi override in an approval entry

- **WHEN** the system loads an approval entry whose pattern contains U+202E
- **THEN** the system rejects the entry with a prohibited-character error

#### Scenario: A Unicode line separator in a field that allows whitespace

- **GIVEN** an approval entry field that allows whitespace
- **WHEN** the field contains U+2028 and no control or bidi character
- **THEN** the system accepts the field

### Requirement: File search excerpts stay on one line

The file search tool SHALL replace each C0 or C1 control character, except
tab, with a space in each match excerpt.

#### Scenario: A carriage return in a matched line

- **WHEN** a matched line contains a CR character
- **THEN** the excerpt contains a space in place of the CR

### Requirement: Identifier segments follow one rule with stated parameters

Every feature that builds a file name, folder name, actor name, or ID from
untrusted text SHALL use one shared rule. Each feature SHALL state the extra
allowed characters, the replacement character, the characters to trim, the
maximum length, and the result for an empty value. The rule SHALL keep each
letter or digit, keep each allowed extra character, and replace every other
character with the replacement character.

#### Scenario: A session folder name

- **WHEN** the system builds the session folder name for the session ID `C0A/123.4`
- **THEN** the folder name is `C0A_123_4`

#### Scenario: A web fetch download file name

- **WHEN** the system names a saved download for `https://example.com/a/b`
- **THEN** the file name starts with `example_com_a_b-`
- **AND** the URL part of the name has no leading or trailing underscore and at most 60 characters

#### Scenario: A reminder actor name from an empty value

- **WHEN** the system builds a reminder actor name from a value that has no allowed characters
- **THEN** the actor name is `reminder`

#### Scenario: A webhook ID from an empty value

- **WHEN** the system builds a webhook ID from an empty value
- **THEN** the system generates a new unique ID

### Requirement: Existing names stay stable across the upgrade

The shared identifier rule SHALL produce, for each feature, the same output
that the feature produced before this change, for every input. Session
folders and webhook IDs that exist before the upgrade SHALL resolve to the
same names after it.

#### Scenario: An existing session folder after the upgrade

- **GIVEN** a session folder that the previous release created
- **WHEN** the upgraded daemon resolves that session
- **THEN** it finds the same folder, with no move or copy
