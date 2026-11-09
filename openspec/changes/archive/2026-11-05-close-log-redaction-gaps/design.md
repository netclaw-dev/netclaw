## Context

See `proposal.md` for the three gaps and the false positive. The current
code:

- `SecretOutputRedactor.Redact` runs one ordered list of rules. Each rule runs
  on the output of the rule before it. Operative sinks and log sinks both call
  it.
- `RedactingLoggerFactory` redacts each record once. It redacts the message,
  and it redacts each structured value alone. Each log writer outside the
  pipeline calls `Redact` itself (see the sink map in
  `docs/architecture/text-safety.md`).
- MEL and the Akka bridge put the message template in the state as
  `{OriginalFormat}`. MEL gives the values in placeholder order. The Akka
  bridge gives them by name, and its formatted message starts with a prefix
  such as `[INFO][...][akka://...]`.

All data in this change is call-local. No actor state and no durable data
change.

## Goals / Non-Goals

**Goals:**

- One log-redaction entry point that every log writer calls.
- Log-only rules that reuse the shared rules and their exceptions, so the
  false-positive behavior stays the same.
- A template rule that uses positions, not text search.
- No measurable cost on an ordinary record.

**Non-Goals:**

- No change to the text that operative sinks receive, apart from the
  `authorizationAttemptId` fix.
- No new secret vocabulary. The log-only rules add no secret words.

## Decisions

### D1. One entry point: `RedactForLogging(string)`

`SecretOutputRedactor.RedactForLogging(text)` runs `Redact`, and then the
log-only rules. `RedactForLogging(Exception)` uses it. Every log writer calls
it: the stage, `SessionLogActor`, `HeadlessChannel`, the crash writers, the
startup failure report, the dispatcher beacon, `ProbeDiagnosticsLog`,
`ToolCallLogFormatter`, the sub-agent log preview, and the memory recall plan
log.

Alternative: a shared rule with an opt-out flag for operative sinks. Rejected:
each new operative caller must then remember the flag, and a missed flag
breaks uploads again (#1992). With a separate entry point, the default for an
operative caller stays safe.

### D2. The next-line rule asks the shared rules

The rule finds `=`, optional spaces or tabs, one line break, optional spaces
or tabs, and a value. A cheap scan for a line break gates it. The key is the
run of key characters before `=` (letters, digits, `_`, `-`, `.`), up to 512
characters. The rule joins the key, `=`, and the value, and calls `Redact` on
the joined text. If `Redact` changes it, the rule masks the value. Each line
break stays in place.

Alternatives:

- A second pass on text with escaped line breaks, as `SubAgentActor` does for
  its preview. Rejected for logs: a value that is masked on one line then also
  masks the first word of the next line, and the record loses its line
  structure.
- A new regex with its own name list. Rejected: it duplicates the shared
  vocabulary and its exceptions (`max_tokens`, `next_token`, `PASSWORD_FILE`,
  `$VAR`), and the two lists drift.

Header lines (`Name:` and then a value) stay out of scope. Prose such as
"Failed to refresh token:" followed by a stack trace would lose its first
frame.

### D3. The signature rule

`[?&;](?:(?:X-Amz-|X-Goog-)?Signature|sig)=<value>`, case-insensitive. The
value ends at `&`, white space, `#`, a quote, `<`, `>`, or `\`. A scan for
`sig` gates it. `;` lets `&amp;sig=` in HTML match. The rule uses `MaskGroup`,
so a placeholder such as `sig={signature}` stays.

### D4. The template rule renders the template and maps the masks

```text
stage (RedactingLogger, call-local):
  redactedMessage = RedactForLogging(message)
  values          = each value redacted alone
  if redactedMessage == message: done          // ordinary record: one compare
  template = state["{OriginalFormat}"]; if none: done
  rendered, spans = Render(template, state)    // span = (start, text, value index)
  masked          = FindLogMasks(rendered)     // one flag per character
  for each span:
    skip if key in {SessionId, SubSessionId, LogSource, ActorPath, AuthorizationAttemptId}
    skip if no masked flag in span
    alone = FindLogMasks(span.text)
    if some flag is masked in context and not alone: value = "***REDACTED***"
```

- `Render` follows MEL: `{{` and `}}` are braces, and `{Name,align:format}`
  takes the value with that name. A repeated name takes the next value with
  that name. Akka gives one value per name, so the last one is used again.
- The rendered template stands in for the formatted message. The Akka prefix
  and format strings change the formatted message in ways that the stage
  cannot map back to placeholders. In the rendered text, each span is exact.
- `FindLogMasks` runs the same rules as `RedactForLogging`, with a mask map.
  Each rule replaces its matches through one helper. With a map, the helper
  records each match and its replacement. The text that the match and the
  replacement share at the start and at the end is kept. The original
  characters between those parts are masked. The map keeps, for each
  character of the current text, the index of its original character, so it
  follows the text through all rules. Without a map, the helper calls
  `Regex.Replace` as before.
- The value-alone check keeps the existing result for a value that holds the
  whole secret shape: `Body = "Authorization: Bearer abc"` stays
  `Authorization: Bearer ***REDACTED***`.
- A masked value becomes the mask text, whatever its type.

Alternatives:

- A substring search for the value text in the redacted message (the first
  #2766 attempt). Rejected: it masked IDs whose text was part of a secret, it
  sent records to a session named `***REDACTED***`, and it missed a secret
  that also appeared unmasked elsewhere.
- A substitution test: replace the value with the mask text and compare the
  outputs. Rejected: it misses a value that is only partly masked, such as
  `Authorization: {Header}` with `Bearer abc`.

### D5. The attempt ID fix in the shared rules

The environment-style rule keeps a name whose last segment is `attempts`, or
whose last two segments are `attempt` and `id`. Segments split at `_`, `-`,
`.`, a space, and a lower-to-upper case change. `password_attempt=hunter2`
stays a secret, because the attempted value can be the password.

## Risks / Trade-offs

- [The log-only rules mask more text.] → A log can lose a signature, a value
  after an empty assignment (`EMPTY_TOKEN=` and then `NEXT=1`), or a
  template-marked value. The operator accepts this cost for terminal sinks.
- [A rule change can make the mask map lose its place.] → The map checks that
  its length equals the redacted text after each rule, and throws if not. The
  stage then withholds the record (fail closed). Tests pin the map for each
  rule kind.
- [The template rule cost.] → It runs only when redaction changed the message.
  It then costs one rendered-text pass with a map, and one value pass for each
  value whose span holds a mask. Measured with `LogRedactionBenchmarks`
  (short job): an ordinary record shows no change in time or allocation (4.6
  us before, 4.5 us after). A record with a secret in its template costs 13.6
  us before and 21.2 us after.
- [The rendered template differs from the formatted message.] → A format
  string or the Akka prefix can change which rules match. Then the attribute
  and the message can differ. For example, a header rule that needs a line
  start matches in the rendered template but not after the Akka prefix. The
  sink map lists this gap.
- [Routing values inside a masked secret.] → The exempt keys are never masked
  by this rule. The message itself stays masked.

## Migration Plan

No stored data changes. Old log files stay as they are. Rollback: revert the
PR.
