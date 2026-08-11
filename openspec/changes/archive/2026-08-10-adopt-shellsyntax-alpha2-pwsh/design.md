## Context

Netclaw starts `/bin/bash -c` on POSIX hosts and `cmd.exe /c` on Windows
hosts. A PowerShell command therefore starts as an outer-host command that can
launch a new `pwsh` or `powershell` child process.

The approval matcher uses ShellSyntaxTree for Bash on POSIX hosts. It uses a
legacy token splitter on Windows hosts. The hard-deny policy uses the same Bash
analysis on POSIX hosts, but it falls back to token segments on Windows hosts.
Neither path has a complete PowerShell occurrence model.

The gate runs before actor dispatch. This change does not alter actor messages,
actor ownership, stored approval records, recovery, or tool arguments.

## Goals / Non-Goals

**Goals:**

- Prove the outer host wrapper before PowerShell analysis.
- Parse complete child payloads with ShellSyntaxTree `0.3.0-alpha.2`.
- Evaluate every PowerShell occurrence in hard-deny and approval policy.
- Keep incomplete and state-dependent forms strict.
- Add review-table evidence for allow, prompt, deny, and stored approval.

**Non-Goals:**

- Change the shell tool schema or execution host.
- Claim isolated PowerShell initial state.
- Read or authorize external `.ps1` file contents.
- Add PowerShell-specific grant records or safe-verb configuration.
- Add profile, module, or `Start-ThreadJob` policy.

## Decisions

### Prove the outer host before parsing the child payload

The analyzer will first use the POSIX Bash grammar. The Bash occurrence must
contain exactly these argv elements in this order:

1. `pwsh`;
2. `-NoProfile`;
3. `-NonInteractive`;
4. `-Command`;
5. one quoted static payload that is not `-`.

The host token must equal `pwsh` with ordinal case-sensitive comparison. The
three PowerShell option names use case-insensitive comparison.

Each element must have complete direct-source provenance. A double-quoted
payload must contain no Bash expansion or escape that can decode differently
under PowerShell rules.

The initial slice will reject every other host spelling, option, or ordering.
This includes `PWSH`, `pwsh.exe`, `powershell`, `powershell.exe`,
`-WorkingDirectory`, `-File`, `-EncodedCommand`,
`-CommandWithArgs`, abbreviated command flags, prefix wrappers, multiple
payloads, trailing arguments, outer redirects, stdin payload `-`, and dynamic
wrapper tokens. A rejected wrapper makes the complete command unresolved.
The detector checks every static authored Bash clause element. It does not use
a finite prefix list, so launcher options cannot move `pwsh` outside the
detected region. This makes command-launching forms such as `builtin command`,
an absolute `env`, and `xargs` strict. A data-only command that uses a standalone
PowerShell host token can therefore receive one-shot approval only.

Windows `cmd.exe` commands keep the legacy matcher for ordinary commands.
ShellSyntaxTree does not parse `cmd` percent expansion, caret escaping, quote
removal, or control operators. A conservative token guard therefore marks a
visible `pwsh` or `powershell` host as complex and returns no approval patterns
or candidates. Safe Windows child reuse needs either a real `cmd` parser or a
direct PowerShell execution path, so it is outside this slice.

Alternative: send the full Bash or `cmd.exe` source directly to `PwshParser`.
That choice can apply PowerShell quote and expansion rules to text that the
outer host changes first.

Alternative: allow all static PowerShell host options. Options such as
`-WorkingDirectory` change the child state and can make Netclaw resolve a path
against the wrong approval directory.

Alternative: accept `powershell` through the same grammar. That name normally
selects Windows PowerShell 5.1, while ShellSyntaxTree targets PowerShell 7.4.
The parser cannot prove a different dialect or a custom shim.

### Use the safe PowerShell initial-state mode

The child payload will use `PwshParserOptions.InitialStateMode = Unknown`.
Netclaw does not disable module auto-load or pin the complete module search
baseline. `-NoProfile -NonInteractive` alone does not meet the parser's
isolated-state contract.

The wrapper proof will still require `-NoProfile` and `-NonInteractive` before
approval reuse. This prevents user profile code and interactive input from
adding hidden behavior. It does not promote loop values or other facts that
need isolated-state proof.

Alternative: assert `IsolatedNonInteractiveNoProfile` from the host flags.
ShellSyntaxTree explicitly rejects that inference because the environment and
module state remain uncontrolled.

### Retain the host occurrence with all child occurrences

After complete wrapper and child analysis, the analyzer will retain the outer
`pwsh` occurrence and add every child `CommandOccurrence`. Bash can replace an
exact `pwsh` token through `BASH_ENV` or an exported function before a child
process starts. The parser does not prove that outer executable resolution.

The wrapper candidate and every child candidate must receive independent
coverage. A safe child can compose with an existing `pwsh` approval. A `pwsh`
approval alone cannot cover a new child command. Prefix executables remain
visible or unresolved.

The hard-deny policy, protected-path policy, and approval matcher will consume
the same analysis. The protected-path policy will inspect decoded exact and
finite child paths and redirect targets in addition to its existing raw-text
deny scan. Each path, redirect, dynamic value, execution region, and
command-resolution change will therefore receive one consistent decision.

Alternative: remove the `pwsh` wrapper like a transparent direct `bash -c`
dispatch. An inherited Bash function can then use a safe child spelling to
hide arbitrary Bash behavior. Keeping all child occurrences prevents the
wrapper grant from covering an arbitrary future payload.

### Keep existing safe-verb and grant shapes

The child occurrence produces the existing `(verb, directory)` candidate.
Native safe verbs such as `git status` can use the current safe list. A
PowerShell cmdlet can use the existing stored approval path when its complete
candidate matches. No new candidate or persisted record field is added.

The POSIX safe list does not gain PowerShell cmdlets. Adding `Get-Content` as a
POSIX executable-safe name would allow an unrelated executable with that name.
PowerShell-native cmdlets on POSIX therefore need an initial approval. The
stored narrow approval can then be reused.

### Keep parser failure atomic

If the outer proof, child parse, command list, occurrence facts, value facts,
or redirect facts are incomplete, the matcher returns no persistent
candidate. The prompt shows the raw command and offers one-shot approval or
deny. The hard-deny policy retains its legacy scan as an additional deny
check, but that fallback cannot authorize the command.

## Risks / Trade-offs

- [Risk] The first wrapper grammar rejects valid PowerShell launch forms. ->
  The command stays one-shot only. Later slices can add proved forms.
- [Risk] PowerShell and the outer host use different quote rules. -> The outer
  grammar supplies the exact child payload. The consumer does not reparse raw
  text under the wrong shell.
- [Risk] Windows keeps its current approval fatigue. -> Windows stays strict
  until Netclaw has a complete `cmd` grammar or a direct PowerShell host.
- [Risk] A host option changes child state. -> The exact argv whitelist rejects
  all options outside the two required host flags and `-Command`.
- [Risk] A host spelling selects another PowerShell dialect. -> The whitelist
  accepts only `pwsh`, the PowerShell 7 host name.
- [Risk] Unknown PowerShell state reduces approval reuse. -> Netclaw does not
  claim stronger facts than its executor provides.
- [Risk] A future package adds an occurrence or enum value. -> Unknown and
  incomplete facts remain strict.
- [Risk] Bash replaces the PowerShell host before launch. -> The outer `pwsh`
  occurrence remains mandatory even when every child is safe or approved.
- [Risk] A hard-deny command or protected path is hidden by outer decoding. ->
  Hard-deny, protected-path, and approval paths share the same complete
  occurrence list. The matrix pins nested deny and decoded protected-path
  cases. Exported-function and `BASH_ENV` cases pin the outer-host boundary.

## Migration Plan

1. Update the central ShellSyntaxTree version.
2. Add the shared PowerShell child analysis.
3. Route hard-deny, protected-path, and approval matching through that analysis.
4. Add focused tests and approval matrix rows.
5. Run security, actor, OpenSpec, header, and Slopwatch checks.

Rollback restores the previous package and analysis path. Stored approvals and
configuration need no conversion.

## Open Questions

None.
