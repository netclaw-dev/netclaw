## RENAMED Requirements

- FROM: `### Requirement: Log redaction uses the shared secret patterns`
- TO: `### Requirement: Log redaction uses the shared patterns and the log-only patterns`

## MODIFIED Requirements

### Requirement: Structured values are redacted and their names are kept

For structured and scope values, the system SHALL redact each value and SHALL
keep each name unchanged. The system SHALL NOT mask a value only because its
name contains a secret word. Numbers, enums, and other convertible primitive
values SHALL keep their type. Other values SHALL become redacted text. The one
exception is a value that its message template marks as a secret. That value
becomes the mask text, whatever its type.

#### Scenario: A secret value in a structured field

- **WHEN** a log record has a structured field `Body` with the value `Authorization: Bearer abc123`
- **THEN** the exported field `Body` has the value `Authorization: Bearer ***REDACTED***`

#### Scenario: A usage count keeps its value

- **WHEN** a log record has a structured field `InputTokens` with the value `1234`
- **THEN** the exported field `InputTokens` has the value `1234`

### Requirement: Log redaction uses the shared patterns and the log-only patterns

Log redaction SHALL apply the shared secret patterns that all Netclaw output
redaction uses, and then the log-only patterns. The log-only patterns SHALL
apply to log sinks only. An operative sink SHALL NOT apply them. Text that
neither set masks SHALL stay unchanged in log sinks.

#### Scenario: An ordinary setting is not masked

- **WHEN** Netclaw logs `max_tokens=4096 total_tokens=1234`
- **THEN** the daemon log contains `max_tokens=4096 total_tokens=1234`

#### Scenario: An authorization attempt ID is not masked

- **WHEN** Netclaw logs `authorizationAttemptId=auth-3f9a1c`
- **THEN** the daemon log contains `authorizationAttemptId=auth-3f9a1c`

#### Scenario: A tool result for the model does not use the log-only patterns

- **GIVEN** a tool result is redacted for the model
- **WHEN** the result contains `?X-Amz-Signature=deadbeef01&X-Amz-Date=20261105T000000Z`
- **THEN** the model receives `X-Amz-Signature=deadbeef01`

### Requirement: Log redaction never changes values that a receiver uses

Log redaction SHALL change only the copy of a value that a terminal sink
receives. A tool result, tool call argument, alert, HTTP response, outbound
request, exception object, or other value that the system later uses SHALL
keep its original content.

#### Scenario: A presigned URL from an MCP tool

- **GIVEN** an MCP tool returns a presigned upload URL that contains `X-Amz-Credential=abc123def456`
- **WHEN** the result is delivered to the model and the result is also logged
- **THEN** the model receives the URL with `X-Amz-Credential=abc123def456`
- **AND** the session log does not contain `abc123def456`

#### Scenario: A presigned URL whose signature comes first

- **GIVEN** an MCP tool returns a presigned upload URL in which `X-Amz-Signature=deadbeefcafe01` comes before `X-Amz-Credential`
- **WHEN** the result is delivered to the model and the result is also logged
- **THEN** the model receives the URL with `X-Amz-Signature=deadbeefcafe01`
- **AND** the session log does not contain `deadbeefcafe01`

#### Scenario: A logged exception is rethrown

- **WHEN** code logs an exception that contains a secret and then rethrows it
- **THEN** the rethrown exception has its original message

### Requirement: Writes outside the logging pipeline redact their output

Crash logs, the crash-log fallback on stderr, the crash monitor fallback on
stderr, the startup configuration failure report, the log-routing failure
notice, the headless CLI session log and its failure log, and the CLI probe
diagnostics log SHALL apply log redaction to all text that they write. Log
redaction is the shared patterns and the log-only patterns. The headless CLI
session log SHALL join streamed deltas the same way as the session log. The
CLI probe diagnostics log SHALL write each entry on one line.

#### Scenario: A crash log for an exception with a secret

- **WHEN** the daemon writes a crash log for an exception whose message contains `password=hunter2`
- **THEN** the crash log contains `password=***REDACTED***`

#### Scenario: The crash log cannot be written

- **GIVEN** the crash log directory cannot be written
- **WHEN** the daemon reports an exception whose message contains `password=hunter2`
- **THEN** the stderr output contains `password=***REDACTED***`

#### Scenario: The headless CLI log gets a presigned URL

- **WHEN** the headless CLI session log writes a tool result that contains `?sig=Zm9vYmFy%3D&sv=2024-11-04`
- **THEN** the headless CLI session log contains `sig=***REDACTED***`

## ADDED Requirements

### Requirement: Log redaction masks a value on the line after its key

When a line ends with a key and `=`, and the next line starts with a value,
log redaction SHALL mask that value if the shared patterns mask the same key
and value on one line. Spaces and tabs MAY come before the line break and
before the value. Each line break SHALL stay in place. A key followed by `:`
is not part of this rule.

#### Scenario: An environment value on the next line

- **WHEN** Netclaw logs `API_KEY=` and a line break and `abc123secret`
- **THEN** the daemon log contains `***REDACTED***` on the continuation line
- **AND** the daemon log does not contain `abc123secret`

#### Scenario: A session log line with a value on the next line

- **WHEN** a user message contains `DB_PASSWORD=` and a line break and `hunter2`
- **THEN** the session log does not contain `hunter2`

#### Scenario: An ordinary setting on the next line

- **WHEN** Netclaw logs `max_tokens=` and a line break and `4096`
- **THEN** the daemon log contains `4096`

### Requirement: Log redaction masks presigned URL signatures

Log redaction SHALL mask the value of the query parameters `X-Amz-Signature`,
`X-Goog-Signature`, `Signature`, and `sig` in any position of the query. The
parameter name SHALL stay. Other query parameters SHALL keep their values,
unless another pattern masks them.

#### Scenario: An S3 signature before the credential

- **WHEN** Netclaw logs a URL with `?X-Amz-Signature=deadbeefcafe01&X-Amz-Credential=abc123def456`
- **THEN** the daemon log contains `X-Amz-Signature=***REDACTED***`
- **AND** the daemon log contains neither `deadbeefcafe01` nor `abc123def456`

#### Scenario: An Azure SAS signature

- **WHEN** Netclaw logs a URL with `?sv=2024-11-04&se=2026-11-06&sig=Zm9vYmFy%3D`
- **THEN** the daemon log contains `sv=2024-11-04&se=2026-11-06&sig=***REDACTED***`

#### Scenario: A parameter that only starts with sig

- **WHEN** Netclaw logs a URL with `?signed=true&sigma=2`
- **THEN** the daemon log contains `?signed=true&sigma=2`

### Requirement: A value that the template marks as a secret is masked in its attribute

When log redaction changes the message of a record, the system SHALL render
the message template with its values and find the characters that log
redaction masks. The system SHALL mask a structured value when its own span
in that text holds a masked character that redaction of the value alone does
not mask. The system SHALL find the span from the placeholder positions in the
template, not from a search for the value text. The system SHALL NOT mask the
values `SessionId`, `SubSessionId`, `LogSource`, `ActorPath`, and
`AuthorizationAttemptId` with this rule. A value that is not in the template
SHALL keep its own redaction only.

#### Scenario: A bearer token in a template

- **WHEN** Netclaw logs the template `Authorization: Bearer {Token}` with the value `abc123xyz`
- **THEN** the message contains `Authorization: Bearer ***REDACTED***`
- **AND** the exported field `Token` has the value `***REDACTED***`

#### Scenario: A value that holds the whole secret shape

- **WHEN** Netclaw logs the template `Refresh failed: {Body}` with the value `Authorization: Bearer abc123`
- **THEN** the exported field `Body` has the value `Authorization: Bearer ***REDACTED***`

#### Scenario: An attempt ID next to a secret

- **WHEN** Netclaw logs the template `authorizationAttemptId={AuthorizationAttemptId} api_key={Key}` with the values `auth-3f9a1c` and `abc123`
- **THEN** the exported field `AuthorizationAttemptId` has the value `auth-3f9a1c`
- **AND** the exported field `Key` has the value `***REDACTED***`

#### Scenario: A routing key inside a masked secret

- **WHEN** Netclaw logs the template `callback token={Token}&session={SessionId}` with the values `abc123` and `D1/1700000000.0001`
- **THEN** the exported field `SessionId` has the value `D1/1700000000.0001`

#### Scenario: The same text at a position that is not a secret

- **WHEN** Netclaw logs the template `user {User} sent Authorization: Bearer {Token}` with the values `abc123` and `abc123`
- **THEN** the exported field `Token` has the value `***REDACTED***`
- **AND** the exported field `User` has the value `abc123`
