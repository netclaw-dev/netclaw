## 1. Review fixes

- [x] 1.1 Keep the webhook route files read-denied; verify the daemon-list and file-tool tests
- [x] 1.2 Exempt only exact read operands from the text screen; verify the jq, python3 -c, and node -e cases stay denied in `ShellConfigReadTests`
- [x] 1.3 Give a writing redirect the write check for its target only; verify `grep ... 2>/dev/null` and `cat cfg > copy.json` are not denied
- [x] 1.4 Run the `shell-config-read` mutation gate and verify every mutant dies
