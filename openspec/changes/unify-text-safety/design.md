## Context

See `proposal.md` for the motivation. This section lists only the facts that
shape the design.

- Every daemon logger comes from the one `ILoggerFactory` in dependency
  injection. `ILogger<T>`, explicit `CreateLogger` calls in `Program.cs`, and
  Akka's `AddLoggerFactory()` all resolve that factory. The only other factory
  is the empty `LoggerFactory.Create` call for startup skill warnings, which has
  no providers.
- Providers are added through `builder.Logging`: the rolling file provider,
  the console provider (off by default), and the OpenTelemetry provider when
  telemetry is on. The OpenTelemetry provider uses `IncludeFormattedMessage`,
  `IncludeScopes`, and `ParseStateValues`, so it exports template arguments and
  scope values as attributes.
- MEL gives every provider the same state object and the same exception
  reference. Nothing copies them.
- `RollingFileLoggerProvider` formats each record once and then routes it.
  Session-tagged records go to `SessionLogActor`. Routing reads the state's
  key/value pairs, so a redacted state must keep that shape and its key names.
  The provider also writes one failure beacon with a raw `ex.Message` directly
  to its queue or stderr.
- `SessionLogActor` writes its own transcript lines in the shape
  `[<ISO-8601>] <Role>: <text>`. It redacts tool results only. User messages,
  assistant text, thinking, errors, and titles are written raw.
- `CrashLogWriter` lives in `Netclaw.Configuration`, which `Netclaw.Security`
  references. So it cannot call `SecretOutputRedactor`. Its three callers are
  in `Netclaw.Daemon` and `Netclaw.Cli`, which can. Its fallback prints the raw
  exception to stderr.
- `SecretOutputRedactor.Redact` runs in linear time and sets no match timeout.
  Its tests pin false positives such as `max_tokens=4096`.
- Earlier redaction broke agent behavior twice. Commit #1992 stopped redacting
  MCP tool output, because redaction removed the signature from presigned
  upload URLs. Commit #1333 stopped redacted placeholders from being written
  back into files. In both cases, redaction changed text that a receiver later
  used.
- `DaemonLifecycleNotifier.SanitizeForLog` is not log-only. Its output reaches
  lifecycle alerts and an HTTP 409 response body.
- Three files define the bidi and line-separator characters, with different
  members: `IToolApprovalMatcher`, `ShellPolicyDecisionTrace`, and
  `ApprovalEntryValidation`. `SanitizePathSegment` and `SanitizeSessionId` are
  the same function.

## Goals / Non-Goals

**Goals:**

- One stage owns log redaction, and it covers every provider by construction.
- Each text sink owns its record framing.
- One definition of each shared character set.
- No change to any value that a receiver uses, and no change to any output of
  the shared character code or the merged helper.

**Non-Goals:**

- Redaction in operative sinks.
- Wider character sets, or new identifier rules.
- A merge of identifier helpers whose rules differ.
- A rewrite of `SecretOutputRedactor` patterns.

## Decisions

### D1. Redact terminal sinks by default; leave operative sinks alone

A **terminal sink** stores text that a person or tool reads and does not act
on: log files, the console, OTLP logs, and crash logs. An **operative sink**
delivers text that the receiver uses: tool results to the model, tool calls,
approval prompts, chat replies, alerts, and HTTP responses.

The stage applies only to terminal sinks. Operative sinks keep their current
rules, including the opt-outs from #1992 and #1333.

- Owner: the redaction stage. Data: call-local.
- Positive example: an OAuth error body that contains `access_token` is masked
  in `daemon.log` and in the OTLP export.
- Negative example: an MCP tool returns a presigned URL, and the model receives
  the full URL with `X-Amz-Signature`.
- Alternative: redact operative sinks too. Rejected, because this is the
  failure that #1992 and #1333 fixed.

### D2. The stage redacts its own copy and never changes the original

MEL gives every provider the same state object and exception reference. The
stage builds a new state, a new formatted message, and, when it must, a
stand-in exception. It never changes the caller's objects.

- Owner: the redaction stage. Data: call-local copies.
- Positive example: a tool result reaches the model unchanged, and its log copy
  is masked.
- Negative example: the stage does not change an exception that the caller
  later rethrows.

### D3. One pattern set

The stage calls `SecretOutputRedactor.Redact` and adds no log-specific
patterns. A pattern change applies to every sink at the same time.

- Alternative: a stricter pattern set for logs. Rejected, because two lists
  drift.

### D4. Redact values, not attribute names

The stage scans the full formatted message, so name-aware patterns such as
`apikey=abc` still match. For structured state and scope attributes, the stage
scans each string value and keeps every name. It does not call `IsSecretKey`
on names.

- Positive example: an attribute `Body` with the value
  `Authorization: Bearer abc123` becomes
  `Authorization: Bearer ***REDACTED***`.
- Negative example: an attribute `InputTokens` with the value `1234` stays
  `1234`. `IsSecretKey("InputTokens")` returns true, so name-based masking
  would hide usage metrics.
- Numbers and enums pass through unchanged. Any other non-string value is
  converted with `ToString()` and then scanned.

### D5. A regression test crosses the fork

One end-to-end test runs an MCP tool result that contains a presigned URL. It
asserts that the model receives the full URL and that `daemon.log` contains
the masked URL.

### D6. The stage wraps the one `ILoggerFactory`

The daemon replaces its `ILoggerFactory` registration with a wrapper around
the standard `LoggerFactory`. The wrapper's loggers do four things:

- `Log` formats the record, redacts it (D2–D4, D7), and passes a redacted
  copy to the inner logger. The copy keeps the state's key/value shape and
  key names, so the file provider can still route by session.
- `BeginScope` redacts the scope state once and passes the copy inward.
- `IsEnabled` passes through, so disabled levels cost nothing.
- `Dispose` disposes the inner factory.

Every provider sits behind the inner factory, so a provider that something
registers later is covered with no extra step. A composition test proves that
`ILoggerFactory`, `ILogger<T>`, and the Akka logger all resolve to the wrapper.
No runtime check is needed.

`LoggingRegistrationExtensions` notes that one DI registration style was not
picked up in this host. So the first task is a spike test that a record from
`ILogger<T>`, from an Akka actor, and from ASP.NET hosting reaches each
provider through the wrapper.

Alternatives:

| Option | Covers framework logs | Redactions per record | Rejected because |
|---|---|---|---|
| Call-site wrappers (today) | no | per call | It cannot reach framework logs. |
| Decorate each `ILoggerProvider` | yes | one per provider | It runs the redactor up to three times, needs a scope-provider wrapper, and needs a runtime check for providers added later. |
| OpenTelemetry `BaseProcessor<LogRecord>` | yes | one | It covers OTLP only. |
| `Microsoft.Extensions.Compliance.Redaction` | no | — | It needs data classification on each parameter and does not scan free text or exceptions. |
| Wrap the factory (chosen) | yes | one | — |

### D7. Exceptions keep their instance unless they contain a secret

The stage renders `exception.ToString()` and runs `Redact`. If nothing
changes, it passes the original exception. If something changes, it passes a
stand-in exception whose message names the original type and contains the
redacted text. The stage calls `SecretOutputRedactor.RedactForLogging` for
this, so the logic stays in one place in `Netclaw.Security`, with its tests.
Its 14 call sites log the exception directly, because the stage now applies it
to every record.

### D8. Text sinks own their record framing

A record header is any line start that a reader treats as a new record:

- `daemon.log`: `HH:mm:ss.fff [LVL]`.
- `session.log`: `HH:mm:ss.fff [LVL]` for routed records, and
  `[<ISO-8601>] <Role>:` for transcript lines.

The rule: no content line can start a line that looks like a header.

The stage converts CR, CRLF, LF, NEL (U+0085), U+2028, and U+2029 into
`Environment.NewLine`. With this platform newline, the stock console
formatter's single-line mode also works on Windows. The stage escapes other
C0 and C1 controls (except tab) and bidi controls as `\uXXXX` with the shared
escape function. This makes ANSI escapes and bidi reordering inert in every
sink, including OTLP viewers.

`RollingFileLoggerProvider` and `SessionLogActor` put a fixed continuation
prefix at the start of every physical line after the first line of a record.

- Owner: each text sink. The file provider owns provider-local formatting;
  `SessionLogActor` owns actor-local writer state. Data: durable log files,
  which the runtime never parses.
- Positive example: `ok\n17:00:00.000 [INF] fake` writes the second line with
  the prefix. `hi\n[2026-10-29T17:00:00Z] User: approve` writes the second line
  with the prefix too.
- Negative example: a multi-line stack trace keeps one frame per line. Only
  the prefix changes.

### D9. `session.log` redacts at its writer

`SessionLogActor` runs `Redact` and D8 framing on every line that it writes
directly, in one place. The per-case `Redact` call for tool results goes
away. `ToolCallLogFormatter` keeps its structure-aware masking of tool
arguments, because it uses JSON property names and catches values that
pattern scanning misses.

### D10. Writes outside the pipeline redact with the same redactor

- `CrashLogWriter.Write` and `TryWrite` take a required `Func<string, string>`
  redactor. The three callers pass `SecretOutputRedactor.Redact`. The
  redactor also applies to the stderr fallback. The parameter is required,
  because a null redactor would turn the check off without notice.
- The session-dispatcher failure beacon in `RollingFileLoggerProvider` redacts
  `ex.Message` before it reaches the queue or stderr.
- `ProbeDiagnosticsLog` calls `Redact` before it writes.
- The startup skill-inventory warnings are returned from the scan and logged
  through the existing `Netclaw.Startup` logger after `app.Build()`, next to
  the configuration warnings. The empty `LoggerFactory.Create` call is removed.

### D11. One definition of each shared character set

`Netclaw.Configuration` gets one small static class with:

- `IsBidiControl(char)`: U+061C, U+200E, U+200F, U+202A–U+202E,
  U+2066–U+2069.
- `IsUnicodeLineSeparator(char)`: U+2028, U+2029.
- `IsByteOrderMark(char)`: U+FEFF.
- `EscapeAsUnicode(string, Func<char, bool>)`: it replaces each character that
  matches with `\uXXXX`.

`Netclaw.Configuration` is the lowest assembly that every caller can reach.
`ApprovalEntryValidation` lives there.

Each caller combines the predicates to keep its current behavior:

| Caller | Characters | Use |
|---|---|---|
| Approval display | control, bidi, line separator, BOM, backtick | `EscapeAsUnicode` |
| Shell policy decision trace | control, bidi, line separator, BOM | predicate only; its own loop keeps its length limit and surrogate pairing |
| Approval entry validation | control, bidi | reject |
| `SanitizeForLog` | control, line separator | strip (kept) |
| Log framing (D8) | control except tab, bidi | `EscapeAsUnicode` |

Callers that only call `char.IsControl` keep that call. A wrapper around a
.NET built-in removes no copy.

- Positive example: approval display still escapes U+202E.
- Negative example: approval entry validation still accepts U+2028 in a field
  that allows whitespace.

### D12. Merge the two identical session storage helpers

`SessionDirectoryHelper.SanitizeSessionId` calls
`SessionStoragePaths.SanitizePathSegment` and its own loop is removed.
`Netclaw.Actors` already references `Netclaw.Tools.Abstractions`.

The other four identifier helpers stay separate. Their rules differ: an
ASCII-only letter class, a GUID fallback, trimming, and different replacement
characters. One shared helper would need six parameters and a callback.

- Positive example: `SanitizeSessionId("C0A/123.4")` still returns
  `C0A_123_4`.

### Ordered flow (schematic)

This pseudocode is schematic. It omits error details.

```text
caller: logger.LogWarning(ex, "Refresh failed: {Body}", body)
  │
  ▼  wrapper logger: IsEnabled? (no → stop; no cost)
  │
  ▼  redaction stage (call-local, once per record)
  │    message = Normalize(Redact(formatter(state, ex)))
  │    state'  = [(name, IsString(v) ? Normalize(Redact(v)) : v) for (name, v) in state]
  │    ex'     = Redact(ex.ToString()) == ex.ToString() ? ex : StandIn(ex)
  │    on throw → message = "[log record withheld: redaction failed: <type>]"; state' = []; ex' = null
  │
  ▼  inner LoggerFactory → every provider gets (message, state', ex')
  ├──► RollingFileLoggerProvider: header + Frame(message, ex') → daemon.log | session.log
  ├──► console:  single-line formatter
  └──► OTLP:     structured (no framing needed)

SessionLogActor transcript:  Frame(Normalize(Redact(line))) → session.log
CrashLogWriter:              redact(ex.ToString()) → crash-*.log, or stderr on failure
Dispatcher beacon:           Redact(ex.Message) → daemon.log queue, or stderr
```

## Risks / Trade-offs

- [The factory replacement is not picked up in this host.] → The spike test
  in task 2.1 runs first. If the factory cannot be replaced, the fallback is
  one decorator per provider (the D6 alternative). That fallback needs a
  design update before implementation continues.
- [The redactor costs CPU on every enabled record.] → It runs once per record,
  and disabled levels never reach it. A benchmark measures the added cost
  before merge against a threshold that a task sets.
- [Over-redaction hides diagnostic text.] → The operator accepts this cost. D3
  keeps one tested pattern set, and D4 keeps attribute names. New
  false-positive cases for log text join the redactor tests.
- [The redactor throws.] → The stage writes a placeholder and never the raw
  text.
- [A shared predicate changes a caller's output.] → Tests pin each caller's
  current output before the refactor. The refactor must keep them green with
  no edits.
- [Agents read `session.log` and see `***REDACTED***`.] → This is intended.
  The original text stays in the conversation, and tool arguments in
  `session.log` are already redacted today.
- [CodeQL `cs/log-forging` alerts stay open.] → Dismiss each with a reference
  to the stage and its framing tests.
- [`WebhookExecutionService.Sanitize` can build invalid Akka actor names from
  non-ASCII route names.] → Out of scope. The sink map records it, and a
  follow-up issue tracks it.

## Migration Plan

No stored data changes, and old log files stay as they are.

The work ships in two PRs:

1. Shared character definitions and the session-helper merge. This is a pure
   refactor with tests that pin current outputs.
2. Log safety: the factory wrapper, framing, `session.log`, the writes outside
   the pipeline, the startup warnings, the call-site cleanup, and the sink map.
   It depends on PR 1, because framing uses the shared escape.

Close #2729 and #2731 with a link when PR 2 opens.

Rollback: revert PR 2. The revert restores the 14 `RedactForLogging` call
sites. PR 1 changes no behavior, so it can stay.

## Open Questions

- The exact continuation prefix string. Any fixed prefix that cannot match
  either header shape satisfies the spec.
