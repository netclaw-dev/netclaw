## Context

See `proposal.md` for the motivation. This section lists only the facts that
shape the design.

- Log records reach five sinks. Four take their input from
  `Microsoft.Extensions.Logging` (MEL): `RollingFileLoggerProvider`
  (`daemon.log`), the console provider (off by default), the OpenTelemetry
  provider (OTLP, when telemetry is on), and `session.log`.
  `RollingFileLoggerProvider.Route` sends session-tagged lines to
  `SessionLogActor`. `SessionLogActor` also writes its own session output lines
  directly. It redacts tool results, but not assistant text or thinking.
- Two writers bypass MEL: `CrashLogWriter` in `Netclaw.Configuration`, and
  `ProbeDiagnosticsLog` in `Netclaw.Cli`. `Netclaw.Security` references
  `Netclaw.Configuration`, so `CrashLogWriter` cannot reference
  `SecretOutputRedactor`. All three `CrashLogWriter` callers are in
  `Netclaw.Daemon` or `Netclaw.Cli`, and both can reference it.
- The OTLP provider uses `IncludeFormattedMessage`, `IncludeScopes`, and
  `ParseStateValues`. It exports each template argument and each scope value
  as an attribute.
- `SecretOutputRedactor.Redact` runs in linear time. Its expensive patterns use
  `RegexOptions.NonBacktracking`, and it sets no match timeout. Its tests pin
  false positives such as `max_tokens=4096` and `total_tokens=1234`.
- Earlier redaction broke agent behavior twice. Commit #1992 turned off
  redaction for MCP tool output, because redaction removed the signature from
  presigned upload URLs. Commit #1333 stopped redacted placeholders from being
  written back into files. In both cases, redaction changed text that a
  receiver later used.
- The control-character copies disagree. `IToolApprovalMatcher` and
  `ShellPolicyDecisionTrace` treat U+2028, U+2029, and U+FEFF as unsafe.
  `ApprovalEntryValidation` does not. `DaemonLifecycleNotifier` ignores bidi
  controls.
- `SanitizePathSegment` and `SanitizeSessionId` are the same function. Four
  other identifier helpers differ only in their parameters.

## Goals / Non-Goals

**Goals:**

- One owner for each rule. Each log sink owns its framing. One stage owns log
  redaction. One module owns each character set and each identifier policy.
- Safe by default. A new log call, including a framework log call, needs no
  extra work.
- No change to any value that a receiver uses.
- No change to the output of modules 2 and 3 at any adopting call site.

**Non-Goals:**

- Redaction in operative sinks, where the receiver uses the value.
- Wider character sets, or new identifier rules. A later change can make these
  on purpose.
- A rewrite of `SecretOutputRedactor` patterns.

## Decisions

### D1. Redact terminal sinks by default; leave operative sinks alone

A **terminal sink** stores text that a person or tool reads and does not act
on: log files, the console, OTLP logs, and crash logs. An **operative sink**
delivers text that the receiver uses: tool results to the model, tool calls,
approval prompts, and chat replies.

The log-safety stage applies only to terminal sinks. Operative sinks keep
their current per-source rules, including the opt-outs from #1992 and #1333.

- Owner: the log-safety stage. Data: call-local.
- Positive example: an OAuth error body that contains `access_token` is masked
  in `daemon.log` and in the OTLP export.
- Negative example: an MCP tool returns a presigned URL. The model receives the
  full URL, including `X-Amz-Signature`. The existing #1992 test continues to
  prove this.
- Alternative: redact operative sinks as well. Rejected, because this is the
  failure that #1992 and #1333 fixed.

### D2. The stage redacts its own copy and never changes the original

MEL passes each record to each provider by value. The stage builds a new state
object, a new formatted message, and, when it must, a stand-in exception. It
never changes the caller's objects. The model, tools, and outbound requests
always get the original values.

- Owner: the log-safety stage. Data: call-local copies.
- Positive example: a tool result reaches the model unchanged, and its log copy
  is masked.
- Negative example: the stage does not change an exception that the caller
  later rethrows. The original instance keeps its message and stack trace.

### D3. One pattern set

The stage calls `SecretOutputRedactor.Redact`. It adds no log-specific
patterns. A pattern change is a change to `SecretOutputRedactor` and its tests,
and it applies to every sink at the same time.

- Alternative: a stricter pattern set for logs. Rejected. Two lists drift, and
  the operator already accepts some lost diagnostic text.

### D4. Redact values, not attribute names

The stage scans the full formatted message, so the name-aware patterns in
`Redact` still apply to text like `apikey=abc`. For structured state and scope
attributes, the stage scans each string value with `Redact`. It keeps every
attribute name, and it does not call `IsSecretKey` on names.

- Positive example: an attribute `Body` with the value `Bearer abc123` becomes
  `Bearer ***REDACTED***`.
- Negative example: an attribute `InputTokens` with the value `1234` stays
  `1234`. `IsSecretKey("InputTokens")` returns true, because the name contains
  "token". Name-based masking would hide usage metrics.
- Values that are not strings, such as numbers and enums, pass through
  unchanged. A value of any other type is converted with `ToString()` and then
  scanned.

### D5. A regression test crosses the fork

One end-to-end test runs one MCP tool result that contains a presigned URL. It
asserts two outcomes: the model receives the full URL, and `daemon.log`
contains the masked URL. This test guards D1 and D2 together.

### D6. The stage decorates every MEL provider

After all logging and telemetry registration, a final registration step
replaces each `ILoggerProvider` service descriptor with a decorator that wraps
the original provider. The daemon calls this step after `AddNetclawTelemetry`,
because `AddOpenTelemetry` registers its provider there.

The decorator also implements `ISupportExternalScope`. It gives the inner
provider a scope provider that redacts scope values.

At startup, the daemon checks that every resolved `ILoggerProvider` is a
decorator. If one is not, the daemon fails to start. This rule follows the "no
silent fallbacks" principle. A provider that something registers later must not
bypass the stage without a visible failure.

Alternatives:

| Option | Covers framework logs | Covers file and console | Rejected because |
|---|---|---|---|
| Call-site wrappers (today) | no | per call | It cannot reach framework logs. Coverage is about 12 of 200 calls. |
| OpenTelemetry `BaseProcessor<LogRecord>` | yes | no | It covers OTLP only. |
| `Microsoft.Extensions.Compliance.Redaction` | no | annotated calls only | It needs data classification on each parameter, and it does not scan free text or exceptions. |
| Decorate each provider (chosen) | yes | yes | — |

### D7. Exceptions keep their instance unless they contain a secret

The stage moves the current `RedactForLogging` logic inside the stage. It
renders `exception.ToString()` and runs `Redact`. If nothing changes, it passes
the original exception, with its type and native stack trace. If something
changes, it passes a stand-in exception whose message names the original type
and contains the redacted text. Then `RedactForLogging` and
`RedactedLoggingException` are removed, and their 14 call sites log the
exception directly.

### D8. Text sinks own their record framing

A record header in `daemon.log` and `session.log` starts with
`HH:mm:ss.fff [LVL]`. The rule is that no content line can start a line that
looks like a header.

The stage normalizes line terminators: CR, CRLF, U+0085, U+2028, and U+2029
become LF. It escapes other C0 and C1 controls (except tab) and bidi controls
as `\uXXXX`, with the module 2 escape policy. This makes ANSI escapes and bidi
reordering inert in every sink, including OTLP viewers.

`RollingFileLoggerProvider` formats each record once. It puts a fixed
continuation prefix at the start of every physical line after the first. Then
it routes the record to `daemon.log` or `session.log`. `SessionLogActor`
applies the same framing to the lines that it writes directly.

The console sink is off by default. A task verifies that its single-line
format keeps one record per line after stage normalization. If it does not,
the console uses a Netclaw formatter with the same continuation prefix.

- Owner: each text sink (provider-local formatting; `SessionLogActor` holds
  actor-local writer state). Data: durable log files. The runtime never parses
  them.
- Positive example: a message `ok\n17:00:00.000 [INF] fake` writes the second
  line with the continuation prefix, so it is not a header.
- Negative example: a normal multi-line stack trace keeps one frame per line.
  Only the prefix changes.

### D9. `session.log` redacts at its writer

`SessionLogActor` runs `Redact` and D8 framing on every line that it writes
directly, in one place. The `Redact` call in the `ToolResultOutput` case
becomes redundant and goes away. Assistant text and thinking become redacted
as a result.

`ToolCallLogFormatter` keeps its structure-aware masking of tool arguments.
That masking uses JSON property names, so it catches values that pattern
scanning misses.

### D10. Writers outside MEL take the redactor as a required parameter

`CrashLogWriter.Write` and `TryWrite` take a required `Func<string, string>`
redactor. The three callers pass `SecretOutputRedactor.Redact`. The parameter
is required because a null redactor would turn the check off without notice.
`ProbeDiagnosticsLog` calls `Redact` and the module 2 replace policy directly.

The startup skill-inventory warnings move to a hosted service that runs at
startup. There they use the host logger, which has the stage.

### D11. Module 2: named character sets that callers combine

The module exposes these sets:

| Set | Characters |
|---|---|
| `C0C1Controls` | `char.IsControl` |
| `BidiControls` | U+061C, U+200E, U+200F, U+202A–U+202E, U+2066–U+2069 |
| `UnicodeLineSeparators` | U+2028, U+2029 |
| `ByteOrderMark` | U+FEFF |

It also exposes three policies: `Strip`, `EscapeAsUnicode` (`\uXXXX`), and
`ReplaceWithSpace`. Each call site combines the sets that it uses today, so
its behavior stays the same. For example:

| Call site | Sets | Policy |
|---|---|---|
| `IToolApprovalMatcher` display | all four, plus backtick | escape |
| `ShellPolicyDecisionTrace` | all four | escape |
| `ApprovalEntryValidation` | `C0C1Controls`, `BidiControls` | reject |
| `DaemonLifecycleNotifier` (removed by D8) | `C0C1Controls`, `UnicodeLineSeparators` | strip |
| `FileSearchTool` excerpt | `C0C1Controls` except tab | replace with space |

The differences between rows are now visible in one table. A later change can
widen a row on purpose.

- Owner: `Netclaw.Configuration`. `ApprovalEntryValidation` lives there, and
  that assembly references only `Netclaw.Media`, so it is the lowest assembly
  that every adopter can reach. A new project would add build and packaging
  overhead for a few pure functions. Data: none (pure functions).
- Positive example: approval display still escapes U+202E.
- Negative example: `ApprovalEntryValidation` still accepts an entry that
  contains U+2028, as it does today. This keeps saved entries loadable.

### D12. Module 3: one identifier helper with explicit parameters

The helper takes the extra allowed characters, the replacement character, the
characters to trim, the maximum length, and the result for an empty value.
Each parameter is required. Each call site states its current values:

| Call site | Allowed | Replace | Trim | Max | Empty result |
|---|---|---|---|---|---|
| `SanitizePathSegment`, `SanitizeSessionId` | letters, digits, `-` | `_` | none | none | empty |
| `SanitizeForFilename` | letters, digits, `-` | `_` | `_` | 60 | empty |
| `SanitizeActorName` | ASCII letters, digits, `_`, `-` | `-` | none | 60 | `"reminder"` |
| `SanitizeWebhookId` | letters, digits, `-`, `_`, `.` | `-` | none | none | new GUID |
| `WebhookExecutionService.Sanitize` | letters, digits | `-` | `-` | none | empty |

"Letters" means `char.IsLetterOrDigit`, which accepts non-ASCII letters, except
in the `SanitizeActorName` row.

- Owner: `Netclaw.Tools.Abstractions`, the lowest assembly that the adopters
  reference. Data: none.
- Positive example: `SanitizeSessionId("C0A/123.4")` still returns `C0A_123_4`,
  so existing session folders keep their names.
- Negative example: an empty webhook ID still produces a new GUID. The design
  keeps this silent fallback for compatibility. The parameter makes it visible
  at the call site.

### Ordered flow (schematic)

This pseudocode is schematic. It omits scope handling and the provider
registration check.

```text
caller: logger.LogWarning(ex, "Refresh failed: {Body}", body)
  │
  ▼  MEL: level enabled?  (no → stop; the stage costs nothing)
  │
  ▼  log-safety stage (call-local, one copy per record)
  │    message   = Normalize(Redact(formatter(state, ex)))
  │    state'    = [ (name, IsString(v) ? Normalize(Redact(v)) : v) for (name, v) in state ]
  │    ex'       = Redact(ex.ToString()) == ex.ToString() ? ex : StandIn(ex)
  │    on throw  → message = "[log record withheld: redaction failed: <type>]"; state' = []; ex' = null
  │
  ├──► RollingFileLoggerProvider: header + Frame(message, ex') → daemon.log | session.log
  ├──► console provider:          one record per line (D8)
  └──► OpenTelemetry provider:    message, state', ex' → OTLP (structured; no framing)

SessionLogActor direct lines:  Frame(Normalize(Redact(line))) → session.log
CrashLogWriter:                redact(ex.ToString()) → crash-*.log   (redact is required)
```

## Risks / Trade-offs

- [The redactor runs on every enabled record, which costs CPU.] → Disabled
  levels never reach the stage. A benchmark measures a typical Information
  record and a long exception against the current daemon. A task sets the
  merge threshold.
- [Over-redaction hides diagnostic text.] → The operator accepts this cost. D3
  keeps one tested pattern set, and D4 keeps attribute names. New
  false-positive cases for log text, such as `InputTokens=1234`, join the
  redactor tests.
- [Something registers a provider after the stage.] → The startup check from D6
  fails the daemon start.
- [The redactor throws.] → The stage fails closed. It writes a placeholder
  record and never the raw text.
- [Module 2 or module 3 changes an output by mistake.] → Before each refactor,
  tests pin the current output of each call site, including edge cases
  (empty, all-invalid, non-ASCII, over 60 characters). The refactor must keep
  these tests green, with no changes to the tests.
- [Agents read `session.log` and now see `***REDACTED***` in assistant text.]
  → This is the intended result. The original text remains in the
  conversation, and tool arguments in `session.log` are already redacted
  today.
- [CodeQL `cs/log-forging` alerts stay open, because CodeQL cannot see the
  stage.] → Dismiss each alert with a reference to the stage and its framing
  tests.
- [`WebhookExecutionService.Sanitize` uses `char.IsLetterOrDigit` for Akka
  actor names, so a non-ASCII route name can produce an invalid actor name.]
  → Module 3 keeps this behavior. The sink map records it, and a follow-up
  issue tracks it.

## Migration Plan

No stored data changes. Modules 2 and 3 keep every output, and old log files
stay as they are.

The work is delivered in three PRs:

1. Module 2. First the output-pinning tests, then the refactor.
2. Module 3. First the output-pinning tests, then the refactor. This PR does
   not depend on PR 1.
3. Module 1: the stage, framing, `session.log`, crash writer, startup
   warnings, removal of the call-site helpers, and
   `docs/architecture/text-safety.md`. Framing uses module 2 sets, so PR 3
   depends on PR 1.

Close #2729 and #2731 with a link when PR 3 opens.

Rollback: revert PR 3. The revert restores the call-site helpers that PR 3
removed. PRs 1 and 2 change no behavior, so they can stay.

## Open Questions

- The exact continuation prefix string. Any fixed prefix that cannot match a
  header satisfies the spec. The implementation picks one, based on what
  reads well in `daemon.log`.
