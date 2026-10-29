## 1. PR 1: Shared character definitions and session helper merge

- [x] 1.1 Add tests that pin current behavior: approval display escaping, shell policy decision trace escaping, approval entry validation (including U+2028 in a field that allows whitespace, and U+202E rejected), `SanitizeForLog` output, and `SanitizeSessionId` output for empty, all-invalid, and non-ASCII input. Verify that all of them pass on the current code before any refactor.
- [x] 1.2 Add `IsBidiControl`, `IsUnicodeLineSeparator`, `IsByteOrderMark`, and `EscapeAsUnicode` in `Netclaw.Configuration` (design D11). Verify set membership and the escape function with unit tests.
- [x] 1.3 Move approval display, the shell policy decision trace, approval entry validation, and `SanitizeForLog` to the shared definitions, and delete their private member lists. Verify that the tests from 1.1 pass with no edits to those tests.
- [x] 1.4 Make `SanitizeSessionId` call `SanitizePathSegment`, and delete its loop (design D12). Verify that the tests from 1.1 pass with no edits.
- [x] 1.5 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`, and verify that both pass. Review the focused mutation scope for approval display escaping, and record the result in the PR.

## 2. PR 2: Log safety (depends on PR 1)

- [ ] 2.1 Spike: replace the daemon `ILoggerFactory` with a pass-through wrapper. Verify with a test that a record from `ILogger<T>`, from an Akka actor, and from ASP.NET hosting reaches the file and OTLP providers through the wrapper. If the factory cannot be replaced, stop and update design D6 before continuing.
- [ ] 2.2 Add a benchmark for a typical Information record and for a long exception through the current pipeline. Record the baseline, and set the merge threshold for added cost in the PR description.
- [ ] 2.3 Implement redaction in the wrapper for the message, structured values, scope values, and exceptions (design D2, D3, D4, D6, D7). Verify the spec scenarios with tests: a framework secret, `Body` masked, `InputTokens` kept, `max_tokens=4096` kept, an exception with and without a secret, a rethrown exception left unchanged, and a sink registered later.
- [ ] 2.4 Implement the fail-closed path. Verify with a test that a redaction failure writes a placeholder and no original text reaches any sink.
- [ ] 2.5 Implement line-break normalization, control and bidi escaping, and the continuation prefix in `RollingFileLoggerProvider` (design D8). Verify the spec scenarios: a fake log header, U+2028, ESC, and a stack trace.
- [ ] 2.6 Make `SessionLogActor` redact and frame every line that it writes directly, and remove its per-case `Redact` call (design D9). Verify that a secret in a user message and in assistant text is masked, that a fake transcript line gets the continuation prefix, and that the conversation that the model sees is unchanged.
- [ ] 2.7 Check the console sink's single-line output against the framing scenarios with a test. If a scenario fails, add a Netclaw console formatter with the same prefix, and verify that the test passes.
- [ ] 2.8 Add the required redactor parameter to `CrashLogWriter`, apply it to the stderr fallback, and update its three callers (design D10). Verify that a crash log and the stderr fallback both mask `password=hunter2`.
- [ ] 2.9 Redact the session-dispatcher failure beacon and the `ProbeDiagnosticsLog` entries (design D10). Verify each with a test that a secret is masked.
- [ ] 2.10 Return the startup skill-scan issues and log them through the existing `Netclaw.Startup` logger, and remove the empty `LoggerFactory.Create` call (design D10). Verify that a rejected skill at startup produces a warning in the daemon log.
- [ ] 2.11 Remove `RedactForLogging` and `RedactedLoggingException`, and make the 14 call sites log the exception directly. Remove the log-only `Redact` calls in `McpClientManager` and `OAuthRefreshGrantHandler`, and the comments that justify redact-before-logging. Move the `RedactForLogging` tests to the stage. Verify that the solution builds and that all tests pass.
- [ ] 2.12 Add the cross-fork regression test (design D5): an MCP result with a presigned URL reaches the model intact, and the same URL is masked in `daemon.log`. Verify that it passes.
- [ ] 2.13 Run the benchmark from 2.2 again. Verify that the added cost is within the threshold, and record both results in the PR.
- [ ] 2.14 Swap in a local daemon build, and exercise an OAuth callback, an MCP failure, and a crash path. Verify that `daemon.log`, `session.log`, and a crash log contain no secrets, and that every line starts with a header or the continuation prefix.
- [ ] 2.15 Review the focused mutation scope for the wrapper registration. Add a narrow Stryker target if a test kills a specific unsafe mutant. Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`, and verify that both pass.

## 3. Documentation and cleanup

- [ ] 3.1 Write `docs/architecture/text-safety.md`. Include every text sink with its owner and rule, the operative sinks that this change keeps, the four identifier helpers kept separate and why, and the known follow-ups. Verify that every sink in the proposal appears in it.
- [ ] 3.2 Add a PRD-002 requirement that diagnostics and log exports exclude secrets, with a revision note. Verify that the proposal's Source PRDs line names it.
- [ ] 3.3 Check whether the `netclaw-operations` skill describes the log line format, log redaction, or crash logs. If it does, update it and bump `metadata.version`. Record the result in the PR description.
- [ ] 3.4 File a follow-up issue for non-ASCII route names in webhook execution actor names, and link it from the sink map. Verify that the issue exists.
- [ ] 3.5 Dismiss each remaining `cs/log-forging` alert with a reference to the stage and its framing tests, not to structured logging. Verify that no such alert stays open without a rationale.
- [ ] 3.6 When PR 2 opens, close PR #2729 and PR #2731 with a link to PR 2. Verify that both are closed.
