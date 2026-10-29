## 1. PR 1: Shared control-character sets (module 2)

- [ ] 1.1 Add tests that pin current behavior for each adopter: approval display escaping, shell policy decision trace escaping, approval entry validation (including U+2028 in a field that allows whitespace), file search excerpts, and tool output spill call IDs. Verify that all of them pass on the current code before any refactor.
- [ ] 1.2 Add the four named sets and the three policies (strip, `\uXXXX` escape, replace with space) in `Netclaw.Configuration`. Verify set membership and each policy with unit tests.
- [ ] 1.3 Move each adopter in design D11 to the shared sets. Verify that the tests from 1.1 pass with no changes to those tests.
- [ ] 1.4 Add an architecture test that fails when a source file outside the module defines a bidi or line-separator member list (for example a literal `'‮'` range). Verify that it fails on a deliberately added copy and passes on the branch.
- [ ] 1.5 Review the focused mutation scope for approval display escaping, as the constitution requires for a security boundary. Record the result in the PR. Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`, and verify that both pass.

## 2. PR 2: Shared identifier segments (module 3)

- [ ] 2.1 Add tests that pin the current output of all six helpers in design D12. Cover these cases: empty, all invalid characters, non-ASCII letters, more than 60 characters, and leading or trailing replacement characters. Verify that they pass on the current code.
- [ ] 2.2 Add the shared helper in `Netclaw.Tools.Abstractions` with required parameters. Verify each parameter with unit tests.
- [ ] 2.3 Move the six adopters to the helper. Verify that the tests from 2.1 pass with no changes to those tests.
- [ ] 2.4 Add an upgrade test: a session folder that the old helper named resolves after the change, with no move or copy. Verify that it passes.
- [ ] 2.5 File a follow-up issue for non-ASCII route names in webhook execution actor names. Verify that the issue exists and that design D12 links it.
- [ ] 2.6 Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`. Verify that both pass.

## 3. PR 3: Log-safety stage and sinks (module 1; depends on PR 1)

- [ ] 3.1 Add a benchmark for a typical Information record and for a long exception through the current logging pipeline. Record the baseline numbers, and set the merge threshold for added cost in the PR description.
- [ ] 3.2 Implement the redaction stage for message, structured values, scope values, and exceptions (design D2, D3, D4, D7). Verify it with tests for the spec scenarios: a framework secret, `Body` masked, `InputTokens` kept, `max_tokens=4096` kept, an exception with and without a secret, and a rethrown exception left unchanged.
- [ ] 3.3 Implement the fail-closed path. Verify with a test that a redaction failure writes a placeholder and no original text reaches any sink.
- [ ] 3.4 Register the stage around every logger provider after telemetry registration, and add the startup check (design D6). Verify with tests that an unwrapped provider stops startup with its name, and that the default configuration with telemetry on wraps the file, console, and OTLP providers.
- [ ] 3.5 Implement framing in the rolling file sink: line-terminator normalization, control and bidi escaping with module 2, and the continuation prefix (design D8). Verify the spec scenarios: a fake header, U+2028, ESC, and a stack trace.
- [ ] 3.6 Check the console sink's single-line output against the framing scenarios. Verify with a test. If a scenario fails, add a Netclaw console formatter that uses the same prefix, and verify that the test passes.
- [ ] 3.7 Make the session log writer redact and frame every line that it writes directly, and remove the per-case `Redact` call (design D9). Verify that a secret in assistant text is masked in `session.log`, and that the conversation that the model sees is unchanged.
- [ ] 3.8 Add the required redactor parameter to `CrashLogWriter`, and update its three callers (design D10). Verify that a crash log for an exception that contains `password=hunter2` is masked.
- [ ] 3.9 Make `ProbeDiagnosticsLog` use the shared redactor and the module 2 replace policy. Verify that a secret is masked and that each entry stays on one line.
- [ ] 3.10 Move the startup skill-inventory warnings to a hosted service that uses the host logger. Verify that a rejected skill at startup produces a warning in the daemon log.
- [ ] 3.11 Remove `RedactForLogging`, `RedactedLoggingException`, `DaemonLifecycleNotifier.SanitizeForLog` and its helpers, and the log-only `Redact` calls. Update the 14 `RedactForLogging` call sites. Verify that the solution builds and that all tests pass.
- [ ] 3.12 Add the cross-fork regression test (design D5): an MCP result with a presigned URL reaches the model intact, and the same URL is masked in `daemon.log`. Verify that it passes.
- [ ] 3.13 Run the benchmark from 3.1 again. Verify that the added cost is within the threshold, and record both results in the PR.
- [ ] 3.14 Swap in a local daemon build and exercise an OAuth callback, an MCP failure, and a crash path. Verify that `daemon.log`, `session.log`, and a crash log contain no secrets, and that every record starts with a header or the continuation prefix.
- [ ] 3.15 Review the focused mutation scope for the stage bypass and the startup check. Add a narrow Stryker target if a test kills a specific unsafe mutant. Run `dotnet slopwatch analyze` and `./scripts/Add-FileHeaders.ps1 -Verify`, and verify that both pass.

## 4. Documentation, planning records, and cleanup

- [ ] 4.1 Write `docs/architecture/text-safety.md`. Include every text sink, its owner, its module, and its rule, plus the operative sinks that this change does not modify and the known follow-ups. Verify that every sink in the proposal appears in it.
- [ ] 4.2 Add a PRD-002 requirement that diagnostics and log exports exclude secrets, and add a revision note. Verify that the proposal's Source PRDs line names the new requirement.
- [ ] 4.3 Check whether the `netclaw-operations` skill describes the log line format, log redaction, or crash logs. If it does, update it and bump `metadata.version`. Verify the result in the PR description.
- [ ] 4.4 Dismiss each remaining `cs/log-forging` alert with a reference to the stage and its framing tests. Do not cite structured logging as the reason. Verify that no such alert stays open without a rationale.
- [ ] 4.5 When PR 3 opens, close PR #2729 and PR #2731 with a link to PR 3. Verify that both are closed.
