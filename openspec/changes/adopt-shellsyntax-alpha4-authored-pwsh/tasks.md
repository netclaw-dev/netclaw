## 1. Dependency and consumer rule

- [x] 1.1 Update the central ShellSyntaxTree version to `0.3.0-alpha.4`.
- [x] 1.2 Accept complete static PowerShell child occurrences without ambient environment proof.
- [x] 1.3 Accept only parser-proved `Write-Output` script-block data while every other dynamic argument stays strict.

## 2. Approval evidence

- [x] 2.1 Add focused analyzer tests for static commands, pipelines, data blocks, unknown values, and source mutations.
- [x] 2.2 Add allow, prompt, and strict cases to the shell approval review matrix.
- [x] 2.3 Update and inspect the approval review-table snapshot.

## 3. Verification and tracking

- [x] 3.1 Update `IMPLEMENTATION_PLAN.md` with the alpha.4 contract and evidence.
- [x] 3.2 Run focused security and actor approval tests.
- [x] 3.3 Run Release build, full tests, header verification, Slopwatch, strict OpenSpec validation, and `git diff --check`.
- [x] 3.4 Run an adversarial review and resolve all material findings.
