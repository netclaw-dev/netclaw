## Context

Netclaw now selects shell behavior in several places. `ShellTool` uses
`cmd.exe` on Windows. Approval code uses Bash analysis on POSIX hosts and a
legacy token path on Windows. A merged POSIX workaround also reparses one
`pwsh -Command` payload with PowerShell grammar.

ShellSyntaxTree `0.3.0-alpha.5` provides an explicit `PwshDialect` contract.
It also defines the host grammar boundary. Bash does not parse PowerShell
payloads, and PowerShell does not parse Bash payloads.

The daemon owns tool execution. Session actors and background-job actors call
the same registered tool and policy objects. Approval records contain a verb
and an optional directory. No persisted actor message contains a shell host.

## Goals / Non-Goals

**Goals:**

- Select one immutable shell environment during daemon composition.
- Use that identity for execution, parsing, policy, approval, and model context.
- Prefer compatible PowerShell 7.6 on Windows and use PowerShell 5.1 as the
  explicit fallback.
- Preserve Bash behavior on Linux and macOS.
- Keep unknown or incomplete shell facts strict.
- Prove the Windows behavior on a native Windows runner.

**Non-Goals:**

- Do not infer a login shell or inspect profile contents.
- Do not parse a child payload with another language grammar.
- Do not add `cmd.exe` grammar or command translation.
- Do not inspect modules, command lookup, inherited variables, or `.ps1` files.
- Do not add a shell selector to the tool schema or configuration.
- Do not add `Start-ThreadJob` or module-version policy.

## Decisions

### Use one immutable environment value

Add a `ShellExecutionEnvironment` value in `Netclaw.Security`. It carries the
platform, executable, grammar, path style, command arguments, and PowerShell
dialect when applicable.

The value creates a parser with the exact working directory and dialect. It
uses `PwshInitialStateMode.Unknown` because `-NoProfile` does not prove the
ambient module or command-resolution state.

Daemon composition resolves this value once. `ShellCommandPolicy` requires it.
`ShellTool`, `ToolAccessPolicy`, `ShellApprovalMatcher`, and the context provider
reuse the same instance through existing composition seams.

Alternative: let each consumer call `OperatingSystem.IsWindows()`. Rejected.
Separate decisions can make the parser authorize text for another executor.

### Resolve the Windows host before service registration

The resolver probes `pwsh.exe` first with `-NoLogo`, `-NoProfile`, and
`-NonInteractive`. It selects PowerShell 7 only for versions `>=7.6.4` and
`<7.7`, which matches ShellSyntaxTree's pinned alias contract.

If the preferred host is absent or incompatible, the resolver probes
`powershell.exe`. It selects `WindowsPowerShell51` only for version 5.1.

The resolver records the selected absolute executable path and dialect. The
tool executes that path and does not repeat `PATH` lookup after authorization.
The resolver logs the reason for a fallback. Daemon startup fails with one
actionable error when neither host satisfies its contract.

The tool does not change hosts during one invocation. If the selected program
later disappears or fails to start, the call fails visibly. A daemon restart
resolves a new immutable environment, then reparses and reauthorizes all new
calls with that environment.

Alternative: retry a failed `pwsh.exe` call through `powershell.exe`. Rejected.
The first approval used PowerShell 7 syntax and aliases. A runtime retry would
execute the text with another grammar after authorization.

### Keep language boundaries at the native host

Linux and macOS use `/bin/bash -c` with `BashParser`. Windows uses the selected
PowerShell host with `-NoLogo -NoProfile -NonInteractive -Command` and
`PwshParser`.

Remove the Netclaw POSIX `pwsh` child-payload parser. Bash treats `pwsh` as an
ordinary external command. Native PowerShell analysis keeps the same-language
recursion that ShellSyntaxTree exposes for `pwsh` and `powershell.exe`.

The parser never reads or executes the submitted command. It analyzes only the
source text and explicit parser options.

Alternative: retain the exact POSIX wrapper workaround. Rejected. It applies
PowerShell meaning below a Bash host and conflicts with the accepted boundary.

### Build process start information once

`ShellTool` has buffered and streaming execution paths. Both paths must call
one process-start builder. The builder uses the environment's absolute
executable path and fixed argument list. It appends the submitted command as
one argument.

Alternative: update both process-start blocks independently. Rejected. One path
could keep `cmd.exe` or use different PowerShell arguments after policy has
authorized the command for the canonical host.

### Build one analysis result for all security consumers

The environment selects one `ShellCommandAnalysis`. Hard deny, protected-path
checks, safe-verb policy, stored approvals, prompt candidates, and display use
that result or the same environment-bound analyzer.

Every complete command occurrence receives policy evaluation. An unparseable
command, an incomplete occurrence, an unknown policy fact, or a dynamic verb
cannot produce a persistent approval candidate or a safe-verb auto-pass.

Legacy token scans remain deny-only backstops. They can detect a known hard
deny in unresolved text, but they cannot authorize or create stored patterns.

PowerShell hard deny adds native equivalents for process termination, recursive
root removal, and `Start-Process -Verb RunAs`. Hard deny remains before path,
safe-verb, and approval checks.

Alternative: keep the Windows token matcher. Rejected. It cannot prove
PowerShell quoting, aliases, pipelines, redirects, or nested execution regions.

### Put the shell identity in the volatile context tail

Extend `WorkingContextSnapshot` with the selected platform, executable,
grammar, and dialect. Render these facts in the existing `[working-context]`
tail for Personal sessions, which are the only shell-capable audience.

The context tells the model which syntax `shell_execute` accepts. It does not
claim that aliases, profiles, modules, environment variables, or external
programs are known.

Alternative: add a second prompt provider. Rejected. The working-context
provider already carries volatile per-turn host facts to sessions and children.

### Preserve actor and persistence contracts

The environment is process composition state. Actors do not persist it and do
not send it in protocol messages. Background jobs use the same registered
`ShellTool` and `ToolAccessPolicy` as direct calls.

Existing approval entries remain valid intent records. A dialect change causes
new parser output before approval matching. Dialect-specific alias
canonicalization prevents a different alias target from reusing an unrelated
stored verb.

No config, tool argument, actor event, snapshot, or approval-store migration is
required.

## Risks / Trade-offs

- [Risk] A Windows host has an unsupported PowerShell 7 version. -> Use the
  explicit 5.1 fallback and show the selected host in logs and model context.
- [Risk] The selected executable changes after startup. -> Fail the call and
  require restart. Never switch grammar after approval.
- [Risk] PowerShell path or provider facts are unknown. -> Withhold persistent
  candidates and safe-verb access. Offer only the existing strict path.
- [Risk] A stored grant was created under another dialect. -> Reparse first and
  match the new canonical candidate. Do not reuse raw source spelling.
- [Risk] Model-visible shell context changes behavior. -> Add deterministic
  context tests and run the shell-platform behavioral evaluation when provider
  credentials are available.
- [Risk] The old Windows approval table encodes `cmd.exe`. -> Replace its host
  cases with reviewed PowerShell 7 and 5.1 outcomes. Do not approve snapshots
  mechanically.

## Migration Plan

1. Land the OpenSpec contract and update the implementation plan.
2. Retire the superseded PowerShell child-wrapper change. Do not sync it.
3. Add the environment resolver and deterministic version-probe tests.
4. Update ShellSyntaxTree and remove the POSIX PowerShell child workaround.
5. Route execution and all policy consumers through the environment.
6. Add the context hint and native Windows approval matrix.
7. Run focused, full, native Windows, header, Slopwatch, and OpenSpec checks.
8. Merge with auto-merge after adversarial review and green CI.

Rollback restores the prior package and `cmd.exe` execution. No persisted data
rollback is necessary. A rollback can increase prompts, but it cannot reinterpret
stored command text because commands are parsed at invocation time.

## Open Questions

None. The host order, version ranges, grammar boundary, context seam, and
failure behavior are fixed by the accepted ShellSyntaxTree contract.
