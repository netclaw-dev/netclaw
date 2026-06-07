## Tasks: Strip bare integer tokens from shell approval patterns

## Phase A: Spec update (OpenSpec change set)

- [x] A.1 Create change directory `fix-1331-integer-verb-chain` with proposal.md
- [x] A.2 Write delta spec for `tool-approval-gates` with updated termination condition and new scenarios
- [x] A.3 Sync delta spec to main `spec.md` via `openspec sync`

## Phase B: Code implementation

- [x] B.1 Add `IsBareIntegerToken()` helper to `IToolApprovalMatcher.cs` — detects pure-digit strings, excludes flags
- [x] B.2 Update `ReconstructClauseText()` to break on bare integer args (termination, not skip)
- [x] B.3 Ensure POSIX path uses AST-based reconstruction; Windows path retains legacy behavior
- [x] B.4 Add 4 new POSIX-only tests: integer stripping, generalization, timeout, candidate verbs

## Phase C: Validation

- [x] C.1 Run full test suite — all 566 tests pass
- [x] C.2 Verify code-analyst review findings addressed (unicode digits noted as low-risk)
- [x] C.3 Run `dotnet slopwatch analyze` — no new violations
- [x] C.4 Run `./scripts/Add-FileHeaders.ps1 -Verify` — headers present

## Phase D: Commit and push

- [x] D.1 Commit code + tests + change set together
- [x] D.2 Push feature branch to origin
