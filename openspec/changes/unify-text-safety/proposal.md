## Why

Text safety in Netclaw lives at each call site, so coverage has gaps and the
rules drift. About 12 of about 200 exception-bearing log calls use
`SecretOutputRedactor.RedactForLogging`, and framework logs cannot use it at
all. PR #2745 found ASP.NET request lines that carried OAuth authorization
codes and SignalR connection tokens, and the OTLP exporter sends log text off
the machine. About 19 private helpers implement three jobs: log safety,
control-character handling, and identifier segments. Copies of one job already
disagree, for example on which bidi characters count as unsafe.

Source PRDs: PRD-002 (SEC-007 Auditability; principle 5 "Keep trust boundaries
simple and inspectable"; threat "accidental over-exposure from
misconfiguration"), PRD-006 (MCP OAuth credential handling).

## What Changes

**Module 1: log safety.**

- Add one redaction stage at the `Microsoft.Extensions.Logging` boundary. It
  applies `SecretOutputRedactor` to every enabled log record before any
  provider receives it: the formatted message, structured state values, scope
  values, and exception text. This covers Netclaw, Akka, ASP.NET, `HttpClient`,
  and SDK log calls.
- Make each text log sink own its record framing, so content cannot start a
  line that looks like a record header. This applies to `daemon.log`, the
  console, and `session.log`. OTLP records are structured and need no framing.
- Make the `session.log` writer redact every line it writes. Today
  `SessionLogActor` redacts tool results but writes assistant text and thinking
  without redaction.
- Route the log writers that bypass `Microsoft.Extensions.Logging` through the
  same redactor: `CrashLogWriter` and `ProbeDiagnosticsLog`.
- Fix the startup skill-inventory warnings in `Program.cs`. They use a
  `LoggerFactory.Create` call that has no providers, so they reach no sink.
- Remove what the stage replaces: `DaemonLifecycleNotifier.SanitizeForLog` and
  its private helpers, `SecretOutputRedactor.RedactForLogging` with
  `RedactedLoggingException` and its 14 call sites, and `Redact` calls whose
  only output is a log record.

**Module 2: control characters.**

- Add one shared module with named character sets: C0/C1 controls, bidi
  controls, Unicode line separators, and the byte-order mark. It also provides
  three policies: strip, `\uXXXX` escape, and replace with a space.
- Replace the private predicates and loops in `IToolApprovalMatcher`,
  `ShellPolicyDecisionTrace`, `ApprovalEntryValidation`, `FileSearchTool`,
  `ToolOutputSpillLocation`, `DaemonLifecycleNotifier`, and
  `ProbeDiagnosticsLog`. Each call site keeps its exact current character set
  and policy.

**Module 3: identifier segments.**

- Add one helper with explicit parameters: the extra allowed characters, the
  replacement character, trimming, the maximum length, and the result for an
  empty value.
- Replace `SanitizePathSegment`, `SanitizeSessionId`, `SanitizeForFilename`,
  `SanitizeActorName`, `SanitizeWebhookId`, and `WebhookExecutionService.Sanitize`.
  Each call site keeps its exact current output, so stored folder names and
  webhook IDs do not change.

**Shared:**

- Add `docs/architecture/text-safety.md`. It maps every text sink to its
  owner, its module, and its rule. It also documents the sinks that this change
  does not modify.
- Supersede PR #2729 and PR #2731. Close both PRs when this change has a PR.

In scope:

- All log text: `daemon.log`, `session.log`, console, OTLP logs, crash logs,
  and the CLI probe diagnostics log.
- The three shared modules, with no change to any output that modules 2 and 3
  produce.
- The sink map document.

Out of scope:

- Operative sinks, where the receiver uses the value: tool results to the
  model, approval prompts, adopted-context envelopes, chat replies, webhook
  payloads. Their redaction rules stay as they are, including the MCP output
  opt-out (#1992) and the `file_read` opt-out (#1333). Modules 2 and 3 change
  only how their code is shared.
- Any change to which characters a call site accepts or rejects, or to the
  identifiers it produces. A later change can widen a set on purpose, with its
  own tests.
- Format escaping (Markdown, JSON, HTML), parsers named `Strip*`, and
  `RegexPromptInjectionDetector`.
- A CodeQL model for the new stage. Remaining `cs/log-forging` alerts are
  dismissed with a reference to the stage and its tests.

## Capabilities

### New Capabilities

- `log-safety`: Redaction and record framing for every log sink. It defines
  the terminal log sinks, what the redaction stage covers, how a sink frames
  its records, and what the stage must not change.
- `text-safety-primitives`: The shared control-character sets and policies,
  and the identifier-segment helper. It defines their contracts, and it
  requires that each adopting call site keeps its current output.

### Modified Capabilities

None. The `mcp-oauth`, `netclaw-tools`, `bounded-tool-output`,
`background-job-execution`, and `tool-authorization` specs keep their current
rules.

## Impact

- Code: about 19 private helpers in 10 assemblies collapse into 3 shared
  modules. Main areas: `Netclaw.Daemon` logging registration and
  `RollingFileLoggerProvider`; `SessionLogActor`; `CrashLogWriter` and its
  three callers; `Netclaw.Security` (`SecretOutputRedactor`,
  `IToolApprovalMatcher`); `Netclaw.Configuration`
  (`ApprovalEntryValidation`); `Netclaw.Actors` tools; webhook code.
- Size: an estimated 200 to 260 lines of existing code removed, and 150 to
  200 lines of shared code added. PR #2729 and PR #2731 (about 350 lines) do
  not merge.
- Performance: the stage runs the redactor on every enabled log record. A
  benchmark is required before merge.
- No new package dependencies.

## Security and Operational Impact

- Security: every log record is redacted by default, including framework
  records, and a new log call is safe without extra work. The OTLP export no
  longer carries raw secrets. `session.log` no longer stores assistant text
  or thinking that repeats a secret.
- Approval and validation behavior does not change. Modules 2 and 3 keep every
  current output, and tests pin those outputs before the refactor.
- Diagnostics: some useful log text becomes `***REDACTED***`. The operator
  accepts this cost. The stage uses the existing pattern set and its
  false-positive tests, so the loss stays narrow.
- The model, tools, and outbound requests get original values. Only log copies
  change.
- PRD gap: PRD-002 has no explicit requirement that diagnostics exclude
  secrets. This change adds that requirement to PRD-002 in the same planning
  cycle.
