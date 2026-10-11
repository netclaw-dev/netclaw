## 1. Confirm the merged behavior

- [x] 1.1 Compare the delta spec with `ShellExecutionEnvironment.cs`, `ManagedTemporaryEnvironment.cs`, and `ShellProcessLaunch.cs` on `dev`, and verify that each stated variable, owner, and condition matches the code
- [x] 1.2 Compare each scenario with `ShellLaunchEnvironmentApprovalTests` and verify that a test covers each positive and negative example

## 2. Update the main specification

- [x] 2.1 Sync the delta into `openspec/specs/session-cwd/spec.md` and verify that `openspec validate --specs` passes
- [x] 2.2 Archive this change and verify that `openspec list` no longer shows it
