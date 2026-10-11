## Why

Source PRDs: `PRD-001` and `PRD-002`.

PR #2309 changed the shell launcher. The launcher now sets `HOME` and `PWD`,
and it removes `CDPATH`. The approval parser resolves `$HOME`, `$TMPDIR`,
`$TMP`, `$TEMP`, and a relative `cd` from the same launch values. The
`session-cwd` specification describes only `TMPDIR`, `TMP`, and `TEMP`. This
change makes the specification agree with the merged behavior.

## What Changes

- State the full set of variables that the shell launcher sets on a Bash
  process: `TMPDIR`, `TMP`, `TEMP`, `HOME`, and `PWD`.
- State that the launcher removes `CDPATH` from a Bash process.
- State that `HOME` is the daemon user profile. The daemon reads it one time
  when it creates the shell environment at startup.
- State that the launcher and the parser read one variable list,
  `ShellExecutionEnvironment.GetLaunchVariables`.
- State when the parser trusts a launch value, and when it does not.
- Give one positive example and one negative example for the trust boundary.

This change is a specification catch-up only. It does not change code. Other
launch behavior and the PowerShell host contract stay out of scope, except
where the specification must say that PowerShell gets no `HOME` or `PWD` fact.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `session-cwd`: The requirement "Every run receives the standard temporary
  environment" gains a related requirement for the launch variables `HOME` and
  `PWD`, the removal of `CDPATH`, and the parser trust boundary.

## Impact

- Code: none. The behavior is in `src/Netclaw.Security/ShellExecutionEnvironment.cs`
  and `src/Netclaw.Actors/Tools/ManagedTemporaryEnvironment.cs`.
- Security: the specification now records the trust boundary for launch
  variables. A command that can change a launch variable makes that value
  unknown, so the parser does not apply the launch value.
- Operations: none.
