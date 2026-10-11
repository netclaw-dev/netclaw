## 1. Rewrite correction

- [x] 1.1 Record an expansion-only exact command in `ShellCommandAnalysis`, copy the fact to the candidate, and read it in `SelectCommandWordsCorrection`; verify that `SubcommandEverywhereGrantTests` gives a correction for `git {push,fetch} origin`
- [x] 1.2 Update the brace credential catalog rows to a correction and accept the review table; verify that `ShellApprovalDispositionMatrixTests` passes

## 2. Control-transfer builtins

- [x] 2.1 Add `exit` and `return` to the Bash control-transfer set; verify that `unattended-cd-or-exit-grant-allows` passes with no `exit` grant

## 3. Tests, mutation gate, and docs

- [x] 3.1 Remove the `AnsiQuoteGap` skip in `ShellConfigReadTests`; verify that the row passes
- [x] 3.2 Restore `Assert.NotEmpty` in the brace matcher test and fix its comment; verify that the Security suite passes
- [x] 3.3 Add a mutation target and test rows for `IsProvedValueDenied`; verify that the shell mutation gate kills each mutant
- [x] 3.4 Document why the rule does not read `MayFieldSplit` in code and in TA-7; verify with `openspec validate`
- [x] 3.5 Run the authorization corpus against dev and list the direction changes in the PR body
