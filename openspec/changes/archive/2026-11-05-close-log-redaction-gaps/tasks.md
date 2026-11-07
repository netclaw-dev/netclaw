## 1. Baseline

- [x] 1.1 Run `LogRedactionBenchmarks` (Release, short job) on the base commit, and record the per-record cost for the PR description.

## 2. Log-only rules (design D1, D2, D3)

- [x] 2.1 Route every shared rule through one replace helper that can record masks, and verify that all existing `SecretOutputRedactor` tests pass with no edits.
- [x] 2.2 Add `RedactForLogging(string)` with the next-line rule and the signature rule, and make `RedactForLogging(Exception)` use it. Verify with `SecretOutputRedactorLoggingTests`: each positive case is masked by the log rules and kept by `Redact`, and each negative case (`max_tokens=`, `next_page_token=`, `$API_KEY`, `token:` prose, `?signed=true&sigma=2`) stays.
- [x] 2.3 Add `FindLogMasks`, and verify with tests that the map marks only the masked characters after rules that change the text length.
- [x] 2.4 Send every log writer through `RedactForLogging`: the stage, `SessionLogActor`, `HeadlessChannel`, the crash writers, `StartupConfigurationFailure`, `DaemonCrashMonitor`, the dispatcher beacon, `ProbeDiagnosticsLog`, `ToolCallLogFormatter`, the sub-agent log preview, and the memory recall plan log. Verify with a log-only shape in the tests of each writer that has tests, and verify that the #1992 tests pass with no edits.

## 3. Shared false positive (design D5)

- [x] 3.1 Keep a name that ends with `attempts` or with `attempt id` in the environment-style rule. Verify that `authorizationAttemptId=auth-3f9a1c` stays and that `password_attempt=hunter2` is masked.

## 4. Template-marked values (design D4)

- [x] 4.1 Add `TemplateMarkedValues` to the stage. Verify with `RedactingLoggerFactoryTests`: a `Token` after `Authorization: Bearer` is masked, a `Header` value of `Bearer abc` is masked, `Body` keeps its own redaction, the exempt keys stay, a value with the same text at a plain position stays, and a scope value outside the template stays.
- [x] 4.2 Verify with `ActorLogRedactionRoutingTests` that an Akka actor record, with its prefix and values by name, masks a template-marked value.

## 5. Proof and documentation

- [x] 5.1 For each new rule, apply a manual mutant that removes it, and verify that a test fails. Restore the rule after each mutant.
- [x] 5.2 Run `LogRedactionBenchmarks` again, and verify that the added cost stays within 0.1% of one core at the observed peak log volume.
- [x] 5.3 Update `docs/architecture/text-safety.md`: the rules, the sink map, the helper table, and the known gaps.
- [x] 5.4 Run `Netclaw.Security.Tests`, `Netclaw.Daemon.Tests`, `Netclaw.Actors.Tests`, `Netclaw.Cli.Tests`, `Netclaw.Configuration.Tests`, the eval parser tests, `dotnet slopwatch analyze`, and `./scripts/Add-FileHeaders.ps1 -Verify`, and verify that all pass.
