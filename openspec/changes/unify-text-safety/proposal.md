## Why

Log redaction lives at each call site, so coverage has gaps. Fourteen of about
200 exception-bearing log calls use `SecretOutputRedactor.RedactForLogging`.
Framework logs cannot use it at all. PR #2745 found ASP.NET request lines that
carried OAuth authorization codes and SignalR connection tokens, and the OTLP
exporter sends log text off the machine. `session.log` stores user messages,
assistant text, thinking, errors, and titles without redaction. Separately,
three files keep private copies of the bidi and line-separator character
lists, and the copies already disagree.

Source PRDs: PRD-002 (SEC-010 Diagnostics Exclude Secrets, added by this
change; SEC-007 Auditability; principle 5 "Keep trust boundaries simple and
inspectable"; threat "accidental over-exposure from misconfiguration"),
PRD-006 (MCP OAuth credential handling).

## What Changes

**Log safety:**

- Wrap the daemon's one `ILoggerFactory` with a redaction stage. Every logger
  that the daemon creates, including Akka and framework loggers, comes from
  this factory. The stage applies `SecretOutputRedactor` once per record to the
  formatted message, structured state values, and exception text. It also
  redacts scope values when a scope begins. Every provider, including one that
  is registered later, receives only redacted records.
- Make each text log sink own its record framing. Content cannot start a line
  that looks like a record header. This covers the `daemon.log` header and the
  `session.log` transcript header (`[<ISO-8601>] <Role>:`).
- Make the `session.log` writer redact every line that it writes directly:
  user messages, assistant text, thinking, errors, titles, and tool results.
- Redact the writes that bypass the logging pipeline: `CrashLogWriter` and its
  stderr fallback, the session-dispatcher failure beacon in
  `RollingFileLoggerProvider`, and `ProbeDiagnosticsLog`.
- Send the startup skill-inventory warnings through the existing startup
  logger. Today they use a `LoggerFactory.Create` call that has no providers,
  so they reach no sink.

**Call-site cleanup (measured):** of 41 redaction call sites, 16 become dead
code under the stage and are removed or simplified:

- 14 `RedactForLogging(ex)` call sites log `ex` directly. `RedactForLogging`
  stays as the one implementation that the stage calls.
- 1 `Redact` call whose only output is a log record is removed
  (`McpClientManager` tool-failure detail).
- 1 per-case `Redact` call in `SessionLogActor` moves into its writer.
- About 5 comments that justify redact-before-logging are removed or reworded.

The other 25 sites stay, because their output goes to an operative sink or to
a writer outside the logging pipeline, or because they redact before a
truncation. `OAuthRefreshGrantHandler` is an example: it redacts each OAuth
error field before it truncates the field, so a cut cannot leave a fragment of a
secret that the stage could no longer match.

**Shared character handling:**

- Add one definition of the bidi control set, the Unicode line separators, and
  the byte-order mark, plus one `\uXXXX` escape function.
- Use them in log framing, approval display, the shell policy decision trace,
  approval entry validation, and `DaemonLifecycleNotifier.SanitizeForLog`. Each
  caller keeps its exact current behavior.
- Merge `SessionDirectoryHelper.SanitizeSessionId` into
  `SessionStoragePaths.SanitizePathSegment`. The two functions are identical.

**Documentation:**

- Add `docs/architecture/text-safety.md`. It maps every text sink to its owner
  and its rule, including the operative sinks and the identifier helpers that
  this change keeps separate on purpose.
- Supersede PR #2729 and PR #2731. Close both PRs when the log-safety PR opens.

In scope:

- All log text: `daemon.log`, `session.log`, console, OTLP logs, crash logs,
  the crash fallback on stderr, and the CLI probe diagnostics log.
- The shared character definitions and the identical-helper merge, with no
  change to any caller's output.

Out of scope:

- Operative sinks, where the receiver uses the value: tool results to the
  model, approval prompts, adopted-context envelopes, chat replies, alerts,
  HTTP responses, and webhook payloads. Their rules stay as they are,
  including the MCP output opt-out (#1992) and the `file_read` opt-out (#1333).
  `SanitizeForLog` stays, because its output also reaches alerts and an HTTP
  409 response.
- The other four identifier helpers (`SanitizeForFilename`,
  `SanitizeActorName`, `SanitizeWebhookId`, `WebhookExecutionService.Sanitize`).
  They encode rules that really differ. The sink map documents them.
- Any change to which characters a caller accepts, rejects, or escapes.
- Format escaping (Markdown, JSON, HTML), `Strip*` parsers, and
  `RegexPromptInjectionDetector`.
- A CodeQL model for the stage. Remaining `cs/log-forging` alerts are
  dismissed with a reference to the stage and its framing tests.

## Capabilities

### New Capabilities

- `log-safety`: Redaction and record framing for every log sink. It defines
  the terminal log sinks, what the redaction stage covers, how a sink frames
  its records, and what the stage must not change.
- `text-safety-primitives`: One shared definition of the bidi, line-separator,
  and byte-order-mark sets, and one rule for session storage names. It requires
  that each caller keeps its current output.

### Modified Capabilities

None. The `mcp-oauth`, `netclaw-tools`, `bounded-tool-output`,
`background-job-execution`, and `tool-authorization` specs keep their current
rules.

## Impact

- Code: `Netclaw.Daemon` logging registration and `RollingFileLoggerProvider`;
  `SessionLogActor`; `CrashLogWriter` and its three callers; `Program.cs`
  startup warnings; `Netclaw.Cli` `ProbeDiagnosticsLog`; `SecretOutputRedactor`;
  16 call sites in `McpClientManager`, `McpOAuthCredentialStore`,
  `WebhookNotificationService`, and `SessionLogActor`; the character-set copies in `IToolApprovalMatcher`,
  `ShellPolicyDecisionTrace`, `ApprovalEntryValidation`, and
  `DaemonLifecycleNotifier`; the two session storage helpers.
- Size (estimate): about 50 to 70 lines of existing code removed, and about
  100 to 140 lines of new code. Three new constructs: the factory wrapper,
  sink framing, and the shared character definitions. PR #2729 and PR #2731
  (about 350 lines) do not merge.
- Performance: the stage runs the redactor once per enabled record. A
  benchmark is required before merge.
- No new package dependencies.

## Security and Operational Impact

- Security: every log record is redacted by default, including framework
  records and records for providers that are registered later. A new log call
  is safe without extra work. The OTLP export and `session.log` no longer
  carry raw secrets.
- Diagnostics: some useful log text becomes `***REDACTED***`. The operator
  accepts this cost. The stage uses the existing pattern set and its
  false-positive tests, so the loss stays narrow.
- The model, tools, alerts, HTTP responses, and outbound requests get their
  current values. Only log copies change.
- PRD coverage: PRD-002 had no explicit requirement that diagnostics exclude
  secrets. This change adds SEC-010 for it.
