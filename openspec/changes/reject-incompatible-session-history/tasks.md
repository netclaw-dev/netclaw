## 1. Compatibility Contract

- [x] 1.1 Add a pure media-to-model compatibility check with an unknown-modality result.
- [x] 1.2 Add a distinct input compatibility error category and user guidance.

## 2. Session Boundary

- [x] 2.1 Reject incompatible current and recovered media before turn admission.
- [x] 2.2 Check active history again before every model call after state changes.
- [x] 2.3 Preserve original media and keep local errors outside provider fallback and alerts.

## 3. Automated Proof

- [x] 3.1 Add unit tests for supported, unsupported, combined, and unknown modalities.
- [x] 3.2 Add actor tests for current media and recovered history with zero provider calls.
- [x] 3.3 Add a tool-message test and a second-boundary actor test.

## 4. Documentation and Gates

- [x] 4.1 Update operator guidance and the `netclaw-operations` system skill.
- [x] 4.2 Run targeted tests, the eval suite, repository quality gates, and OpenSpec validation.
- [x] 4.3 Update this checklist with final verification evidence.

## Verification Evidence

- Focused compatibility suite: 10 tests passed.
- `Netclaw.Actors.Tests`: 2,657 tests passed.
- `dotnet test Netclaw.slnx --no-restore`: all enabled tests passed.
- `dotnet slopwatch analyze`: 0 issues.
- `pwsh ./scripts/Add-FileHeaders.ps1 -Verify`: passed.
- `openspec validate reject-incompatible-session-history --strict`: passed.
- `git diff --check`: passed.
- Changed production and new test files pass the scoped format check.
- The full format check still reports pre-existing repository format debt.
- `./evals/run-evals.sh` could not start because no `NETCLAW_EVAL_*` target exists in this environment.
