## 1. Agent-Facing Path Contract

- [x] 1.1 Remove `media_dir` from trusted session context while preserving `session_dir` and Public path redaction.
- [x] 1.2 Update attachment guidance to define the announced collision-safe `inbox/...` path as authoritative and relative to `session_dir`.

## 2. Contract Tests

- [x] 2.1 Prove live collision-renamed announcements resolve to their inbox files.
- [x] 2.2 Prove historical stable announcements resolve to their inbox files.
- [x] 2.3 Prove internal GUID media filenames do not appear in agent-facing attachment metadata.
- [x] 2.4 Update session context tests for the single exposed filesystem root.

## 3. Validation

- [x] 3.1 Validate the OpenSpec change and run targeted actor/channel tests.
- [x] 3.2 Attempt the behavioral evals required for system-prompt assembly changes; execution is blocked because the required `NETCLAW_EVAL_*` provider environment is absent.
- [x] 3.3 Run Slopwatch, copyright header verification, and `git diff --check`.
