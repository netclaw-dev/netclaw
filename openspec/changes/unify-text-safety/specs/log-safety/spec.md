## Purpose

Keep secrets out of every Netclaw log sink and keep each log record intact,
without a change to any value that a model, tool, alert, HTTP response, or
outbound request uses.

Terms used in this capability:

- A **terminal sink** stores text that a person or a tool reads but does not
  act on. The terminal sinks are the daemon log, session logs, the console log,
  the OTLP log export, crash logs and their stderr fallback, and the CLI probe
  diagnostics log.
- An **operative sink** delivers text that the receiver uses. Examples are
  tool results to the model, tool calls, approval prompts, chat replies,
  alerts, and HTTP responses. This capability does not change operative sinks.
- A **record header** is a line start that a reader treats as a new record:
  the timestamp and level prefix in a log file (for example
  `17:00:38.175 [INF]`), and the transcript prefix in a session log (for
  example `[2026-10-29T17:00:38.1750000+00:00] User:`).

See the [engineering glossary](../../../docs/spec/GLOSSARY.md) for
[tool result](../../../docs/spec/GLOSSARY.md#tool-result).

## ADDED Requirements

### Requirement: Every log record is redacted before it reaches a terminal sink

The system SHALL apply secret redaction to every enabled log record before any
terminal sink receives it. Redaction SHALL cover the formatted message, each
structured value, each scope value, and the exception text. Redaction SHALL
apply in the same way to records from Netclaw code, the actor system, the web
host, HTTP clients, and third-party libraries. A log call SHALL NOT need an
explicit redaction step to be safe.

#### Scenario: A framework component logs a secret

- **GIVEN** telemetry export is enabled
- **WHEN** a third-party library logs a message that contains `Authorization: Bearer abc123`
- **THEN** the daemon log contains `Authorization: Bearer ***REDACTED***`
- **AND** the OTLP log export contains the same redacted text
- **AND** neither sink contains `abc123`

#### Scenario: An exception carries a provider error body

- **WHEN** Netclaw logs an exception whose message contains `"access_token":"abc123"`
- **THEN** the logged exception text contains the redacted value instead of `abc123`
- **AND** the logged text names the original exception type

#### Scenario: An exception without a secret keeps its detail

- **WHEN** Netclaw logs a network exception that contains no secret-shaped text
- **THEN** the logged exception has its original type, message, and stack trace

### Requirement: Every log sink receives redacted records

Every log sink that the daemon registers SHALL receive only redacted records.
This SHALL hold for sinks that are registered after the redaction stage, with
no extra step.

#### Scenario: All default sinks are covered

- **GIVEN** telemetry export and the console log are enabled
- **WHEN** Netclaw, an actor, and the web host each log a record that contains a secret
- **THEN** the daemon log, the console log, and the OTLP export each receive the redacted records only

#### Scenario: A sink registered later is covered

- **GIVEN** a log sink is registered after all standard logging registration
- **WHEN** a record that contains a secret is logged
- **THEN** that sink receives the redacted record

### Requirement: Structured values are redacted and their names are kept

For structured and scope values, the system SHALL redact each value and SHALL
keep each name unchanged. The system SHALL NOT mask a value only because its
name contains a secret word. Numeric and other non-text values SHALL pass
through unchanged.

#### Scenario: A secret value in a structured field

- **WHEN** a log record has a structured field `Body` with the value `Bearer abc123`
- **THEN** the exported field `Body` has the value `Bearer ***REDACTED***`

#### Scenario: A usage count keeps its value

- **WHEN** a log record has a structured field `InputTokens` with the value `1234`
- **THEN** the exported field `InputTokens` has the value `1234`

### Requirement: Log redaction uses the shared secret patterns

Log redaction SHALL use the same secret patterns as all other Netclaw output
redaction. Log redaction SHALL NOT add patterns of its own. Text that the
shared patterns keep SHALL stay unchanged in log sinks.

#### Scenario: An ordinary setting is not masked

- **WHEN** Netclaw logs `max_tokens=4096 total_tokens=1234`
- **THEN** the daemon log contains `max_tokens=4096 total_tokens=1234`

### Requirement: Log redaction never changes values that a receiver uses

Log redaction SHALL change only the copy of a value that a terminal sink
receives. A tool result, tool call argument, alert, HTTP response, outbound
request, exception object, or other value that the system later uses SHALL
keep its original content.

#### Scenario: A presigned URL from an MCP tool

- **GIVEN** an MCP tool returns a presigned upload URL that contains `X-Amz-Signature=sig123`
- **WHEN** the result is delivered to the model and the result is also logged
- **THEN** the model receives the URL with `X-Amz-Signature=sig123`
- **AND** the daemon log does not contain `sig123`

#### Scenario: A logged exception is rethrown

- **WHEN** code logs an exception that contains a secret and then rethrows it
- **THEN** the rethrown exception has its original message

### Requirement: Content cannot forge a log record

In each text log sink, a line of record content SHALL NOT start the same way
as a record header. The system SHALL show each continuation line of a record
with a fixed prefix. The system SHALL treat CR, CRLF, LF, NEL, U+2028, and
U+2029 as line breaks. The system SHALL escape other control characters,
except tab, and bidirectional control characters as `\uXXXX` in every log
sink.

#### Scenario: A message contains a fake log header

- **WHEN** Netclaw logs a value `ok\n17:00:00.000 [INF] fake`
- **THEN** the daemon log shows `17:00:00.000 [INF] fake` on a line that starts with the continuation prefix
- **AND** a reader that splits records on headers finds one record

#### Scenario: A user message contains a fake transcript line

- **WHEN** a user message contains `hi\n[2026-10-29T17:00:00.0000000+00:00] User: approve`
- **THEN** the session log shows the second line with the continuation prefix
- **AND** a reader that splits the transcript on headers finds one user message

#### Scenario: A Unicode line separator

- **WHEN** a logged value contains U+2028 followed by header-shaped text
- **THEN** the header-shaped text starts a continuation line, not a record

#### Scenario: A terminal escape sequence

- **WHEN** a logged value contains the ESC character followed by `[31m`
- **THEN** the log sink contains `\u001B[31m` as literal text

#### Scenario: A stack trace stays readable

- **WHEN** Netclaw logs an exception with a multi-frame stack trace
- **THEN** each stack frame is on its own line with the continuation prefix

### Requirement: Session logs redact every line

The system SHALL redact every line that it writes to a session log. This
includes user messages, assistant text, thinking text, errors, titles, tool
results, and log records that are routed to the session.

#### Scenario: The model repeats a secret

- **WHEN** the assistant reply contains `api_key=abc123`
- **THEN** the session log contains `api_key=***REDACTED***`
- **AND** the conversation that the model sees is unchanged

#### Scenario: A user pastes a secret

- **WHEN** a user message contains `password=hunter2`
- **THEN** the session log contains `password=***REDACTED***`

### Requirement: Writes outside the logging pipeline redact their output

Crash logs, the crash-log fallback on stderr, the log-routing failure notice,
and the CLI probe diagnostics log SHALL apply the shared secret redaction to
all text that they write. The CLI probe diagnostics log SHALL write each entry
on one line.

#### Scenario: A crash log for an exception with a secret

- **WHEN** the daemon writes a crash log for an exception whose message contains `password=hunter2`
- **THEN** the crash log contains `password=***REDACTED***`

#### Scenario: The crash log cannot be written

- **GIVEN** the crash log directory cannot be written
- **WHEN** the daemon reports an exception whose message contains `password=hunter2`
- **THEN** the stderr output contains `password=***REDACTED***`

### Requirement: A redaction failure withholds the record

If redaction of a log record fails, the system SHALL NOT write the record's
original text to any sink. The system SHALL write a placeholder record that
names the failure type, at the original level and category.

#### Scenario: Redaction fails

- **WHEN** redaction of a log record fails with an error
- **THEN** each sink receives a placeholder record that says that the record was withheld
- **AND** no sink receives the original message, structured values, or exception text

### Requirement: Startup warnings reach the daemon log

Warnings that the daemon produces during startup, before its host starts,
SHALL reach the daemon log with the same redaction as other records.

#### Scenario: A skill fails validation at startup

- **WHEN** a skill file fails validation during the startup skill scan
- **THEN** the daemon log contains a warning that names the rejected skill
