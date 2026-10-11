## 1. Review fixes

- [x] 1.1 Keep the denial for a word with a brace in the text screen and the read-only operand rule; verify the brace rows in `ShellConfigReadTests`
- [x] 1.2 Match the guarded-directory markers on the collapsed text and the unquoted argument values; verify the `//`, `/./`, `name/../`, and split-quote rows
- [x] 1.3 Fix the TA-6 scenario, the directory rule, and the traceability row
- [x] 1.4 Run the `shell-config-read` mutation gate and verify every mutant dies
