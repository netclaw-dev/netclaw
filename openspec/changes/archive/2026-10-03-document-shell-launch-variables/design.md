## Context

See `proposal.md` - Why. The behavior already exists on `dev`. This design
records the owners, the data scope, and the order of the launch steps. It does
not change code.

## Goals / Non-Goals

**Goals:**

- Record which component owns each launch value.
- Record the order in which the launcher and the parser read the values.

**Non-Goals:**

- No code change, no new variable, and no change to the PowerShell host.
- No change to the full list of Bash startup variables that the launcher
  removes. This change names only `CDPATH`, because a relative `cd` depends on it.

## Decisions

### One variable list for the launcher and the parser

`ShellExecutionEnvironment.GetLaunchVariables` returns the temporary variables
and `HOME`. The launcher writes these values to the child process. The parser
gets the same list through `ShellExecutionEnvironment.CreateLaunchEnvironment`.
The launcher writes `PWD` from the same working directory value that
`ShellProcessLaunch` gives to the parser.

Alternative: compute the parser values separately. We rejected this, because
the two copies can drift. A drift lets the parser approve a path that the shell
does not use.

### Owners and data scope

| Value | Owner | Scope |
|-------|-------|-------|
| `HOME` | `ShellExecutionEnvironment` | Process-local. The daemon reads it one time at startup. |
| `TMPDIR`, `TMP`, `TEMP` | `ManagedTemporaryEnvironment` with the run's `ManagedTemporaryLocation` | Call-local. The session storage envelope holds the directory durably. |
| `PWD` | `ShellExecutionEnvironment.ApplyWorkingDirectory` | Call-local. |
| `CDPATH` removal | `ShellExecutionEnvironment.RemoveBashStartupOverrides` | Call-local. |
| Trust decision | The Bash parser, from the launch facts | Call-local. Nothing persists. |

### Launch order

Schematic. This flow omits the authorization gates that come before the launch.

```text
CreateProcessStartInfo(command)
  remove Bash startup overrides, including CDPATH   (Bash only)
  set HOME from GetLaunchVariables(no temp)        (Bash only, if absolute)
StartAsync
  Analyze(command, workingDirectory, temp)         -> parser reads GetLaunchVariables(temp)
  ManagedTemporaryEnvironment.Prepare              -> set TMPDIR, TMP, TEMP or fail
  ApplyWorkingDirectory                            -> set cwd and PWD (Bash only)
  start the process
```

## Risks / Trade-offs

- [The daemon user profile changes while the daemon runs] → The launcher and
  the parser both keep the startup value. They stay equal until restart.
- [A future launcher variable is set outside the shared list] →
  `ShellLaunchEnvironmentApprovalTests.Launched_shell_receives_the_values_that_the_parser_resolved`
  runs the real shell and compares each value with the parser value.
- [Temporary directory preparation fails] → The process does not start. The
  launcher does not fall back to the host temporary root.
