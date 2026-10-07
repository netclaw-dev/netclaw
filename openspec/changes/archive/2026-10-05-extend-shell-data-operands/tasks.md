## 1. Policy

- [x] 1.1 Add `IsDataCommand` and `BashTestBuiltins`, and use them in the matcher, the exemption, and the analysis; verify with `ShellCommandAnalysisMutationTests.Test_builtin_operand_is_data_only_with_a_bounded_value_without_a_subscript`
- [x] 1.2 Skip the assignment digest for a Bash data command with no redirect; verify with the catalog case `echo-substitution-value-is-data`
- [x] 1.3 Add the positive and negative catalog cases and verify that `ShellApprovalDispositionMatrixTests` passes with the updated review table

## 2. Gates and docs

- [x] 2.1 Add the new spans to `scripts/run-shell-command-analysis-mutations.sh` and verify that every mutant dies
- [x] 2.2 Update `TOOLING.md`, the approval runbook, and the `netclaw-operations` skill (version bump), and verify the text against the catalog cases
- [x] 2.3 Run `openspec validate extend-shell-data-operands --strict`, sync with `/opsx:sync`, and archive with `/opsx:archive`
