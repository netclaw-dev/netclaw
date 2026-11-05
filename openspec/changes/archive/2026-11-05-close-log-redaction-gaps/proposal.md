## Why

An independent review of #2766 found three secret shapes that reach
`daemon.log`, `session.log`, and the OTLP export (#2770):

- A key and its value on separate lines: `API_KEY=` and then the value on
  the next line.
- A presigned URL signature: an S3 `X-Amz-Signature` that comes before
  `X-Amz-Credential`, and an Azure SAS `sig=` value.
- A structured value that only the message template marks as a secret:
  `"Authorization: Bearer {Token}"` masks the message but not the `Token`
  attribute.

The shared patterns also mask `authorizationAttemptId=auth-<hex>`. About 20
log calls use this ID for OTLP correlation (#2589).

Operative sinks use the shared patterns too. A new signature pattern in the
shared set would mask presigned URLs that tools return to the model, which is
the failure that #1992 fixed. Thus the new patterns must apply to logs only.

Source PRDs: PRD-002 SEC-010 (Diagnostics Exclude Secrets) and SEC-007
(Auditability).

## What Changes

- Add a log-only pattern set. Log redaction applies the shared patterns and
  then the log-only patterns. Operative sinks keep the shared patterns only.
  - A key at the end of a line, with `=`, and its value on the next line. The
    value is masked when the shared patterns mask the same key and value on
    one line.
  - The signature query parameter of a presigned URL in any position:
    `X-Amz-Signature`, `X-Goog-Signature`, `Signature`, and the Azure SAS
    `sig`.
- Add one log-redaction entry point, and send every log writer through it:
  the log stage, session log transcript lines, the headless CLI session log
  and its failure log, crash logs and their stderr fallbacks, the startup
  configuration failure report, the dispatcher beacon, the CLI probe
  diagnostics log, the tool call log formatter, the sub-agent log preview,
  and the memory recall plan log.
- Mask a structured value when the template around it marks it as a secret.
  The stage renders the template with its values and finds the characters
  that log redaction masks. A value is masked when its own span holds a
  masked character that redaction of the value alone does not mask. The
  routing keys (`SessionId`, `SubSessionId`, `LogSource`, `ActorPath`) and
  `AuthorizationAttemptId` are never masked by this rule.
- Fix the shared false positive: a name that ends with "attempt id" (for
  example `authorizationAttemptId`) names an identifier, not a secret.

In scope:

- All log sinks that the `log-safety` capability names.
- The one shared-pattern false positive above.

Out of scope:

- Any new pattern for operative sinks. Tool output to the model keeps the
  shared patterns only.
- Header lines (`Name:` and the value on the next line) in the multi-line
  rule. Prose such as "Failed to refresh token:" and then a stack trace would
  lose diagnostic text.
- Header rules that need a line start, when an Akka prefix comes before the
  header text.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `log-safety`: log redaction adds a log-only pattern set; a value that the
  template marks as a secret is masked in its attribute; writes outside the
  logging pipeline use log redaction; ordinary IDs such as
  `authorizationAttemptId` stay unmasked.

## Impact

- Code: `Netclaw.Security` (`SecretOutputRedactor`, `ToolCallLogFormatter`),
  `Netclaw.Daemon` (`RedactingLoggerFactory`, crash and startup writers, the
  dispatcher beacon), `Netclaw.Actors` (`SessionLogActor`, `SubAgentActor`,
  `SQLiteMemoryRecallCoordinator`), `Netclaw.Cli` (`HeadlessChannel`,
  `ProbeDiagnosticsLog`, crash writer).
- Docs: `docs/architecture/text-safety.md` (rules, sink map, known gaps).
- Security: logs mask more secret shapes. Operative output does not change,
  except that `authorizationAttemptId=` values are no longer masked anywhere.
- Operations: a log line can show `***REDACTED***` where it showed a
  signature, a next-line value, or a template-marked attribute before.
- Performance: the log-only rules run on every log record. The template rule
  runs only on a record whose message log redaction changed.
